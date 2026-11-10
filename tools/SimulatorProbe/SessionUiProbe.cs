using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.SimulatorProbe;

internal static class SessionUiProbe
{
    internal static async Task<IAsyncDisposable> ObserveLiveAsync(DirectorSessionContainer session, bool verifyLifecycle)
    {
        if (verifyLifecycle) await VerifyGlobalTriggerStatusAsync();
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var chart = new NINA.WPF.Base.View.AltitudeChart { Height = 220 };
            chart.SetBinding(FrameworkElement.DataContextProperty, new Binding(nameof(session.SkyTarget)) { Source = session });
            chart.SetBinding(NINA.WPF.Base.View.AltitudeChart.NighttimeDataProperty, new Binding(nameof(session.SkyNighttime)) { Source = session });
            var history = new DataGrid { Height = 220, IsReadOnly = true, AutoGenerateColumns = true };
            history.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(session.ActionHistory)) { Source = session });
            // Bind the actual N.I.N.A. trigger collection, including temporary
            // triggers, to exercise the same CollectionView as the editor.
            var triggers = new ItemsControl { ItemsSource = ((SequentialContainer)session).Triggers, DisplayMemberPath = "Name" };
            var content = new StackPanel();
            content.Children.Add(new Button { Content = "Report equipment", Command = session.ReportEquipmentCommand });
            content.Children.Add(triggers);
            content.Children.Add(chart);
            content.Children.Add(history);
            var window = new Window
            {
                Title = "Director simulator live view",
                Width = 700,
                Height = 500,
                Owner = Application.Current.MainWindow,
                Content = content
            };
            if (verifyLifecycle) session.Add(new EditorBoundCondition());
            window.Show();
            return (IAsyncDisposable)new LiveView(window);
        });
    }

    private sealed class EditorBoundCondition : SequenceCondition
    {
        public EditorBoundCondition() => Name = "Editor-bound condition";
        public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem) => true;
        public override object Clone() => new EditorBoundCondition();
        public override void SequenceBlockInitialize() => Application.Current.Dispatcher.VerifyAccess();
        public override void SequenceBlockStarted() => Application.Current.Dispatcher.VerifyAccess();
        public override void SequenceBlockFinished() => Application.Current.Dispatcher.VerifyAccess();
        public override void SequenceBlockTeardown() => Application.Current.Dispatcher.VerifyAccess();
    }

    private sealed class LiveView(Window window) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await window.Dispatcher.InvokeAsync(window.Close);
    }

    internal static Task RenderAsync(string directory, DirectorSessionDisplay? display = null, DirectorSessionContainer? source = null) => Application.Current.Dispatcher.InvokeAsync(() =>
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // NINA gives each plugin its own AssemblyLoadContext. The test plugin's
        // reference has a different CLR identity from the actual exported type.
        var template = Templates(Application.Current.Resources).SingleOrDefault(value =>
            value.DataType is Type type && type.FullName == typeof(DirectorSessionContainer).FullName)
            ?? throw new InvalidOperationException("NINA did not discover the exported Director session template.");
        var exportedType = (Type)template.DataType;
        foreach (var width in new[] { 640, 1000 })
        {
            var session = Activator.CreateInstance(exportedType)!;
            if (source is not null)
            {
                exportedType.GetMethod("ShowSky", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [source.SkyTarget, source.SkyNighttime]);
                var record = exportedType.GetMethod("RecordAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (var entry in source.ActionHistory.Reverse()) record.Invoke(session, [entry.Target, entry.Action, entry.Outcome, entry.ElapsedMs]);
            }
            if (display is not null)
            {
                var report = exportedType.GetMethod("Report", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var nativeDisplay = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(display), report.GetParameters()[0].ParameterType);
                report.Invoke(session, [nativeDisplay]);
            }
            var beforeTarget = (NINA.Sequencer.Container.SequentialContainer)exportedType.GetProperty("BeforeNewTarget")!.GetValue(session)!;
            beforeTarget.Add(new WaitForTimeSpan { Time = 1 });
            var profile = new Moq.Mock<NINA.Profile.Interfaces.IProfileService> { DefaultValue = Moq.DefaultValue.Mock };
            var guider = Moq.Mock.Of<NINA.Equipment.Interfaces.Mediator.IGuiderMediator>(g => g.GetInfo() ==
                new NINA.Equipment.Equipment.MyGuider.GuiderInfo { Connected = true });
            var editorTrigger = new NINA.Sequencer.Trigger.Guider.DitherAfterExposures(guider,
                Moq.Mock.Of<NINA.WPF.Base.Interfaces.ViewModel.IImageHistoryVM>(), profile.Object)
            { Name = "Dither after exposures", AfterExposures = 3 };
            ((SequentialContainer)session).Add(editorTrigger);
            var editorCondition = new LoopCondition { Iterations = 5, Name = "Loop count" };
            ((SequentialContainer)session).Add(editorCondition);
            var content = (FrameworkElement)template.LoadContent();
            content.DataContext = session;
            var host = new Border { Width = width, Background = (Brush)Application.Current.FindResource("BackgroundBrush"), Child = content };
            System.Windows.Documents.TextElement.SetForeground(host, (Brush)Application.Current.FindResource("PrimaryBrush"));
            Layout();
            var tabs = Descendants(host).OfType<TabControl>().Single();
            if (!Equals(((TabItem)tabs.SelectedItem).Header, "Overview"))
                throw new InvalidOperationException("Director must open on its current status.");
            if (display is null)
            {
                tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => Equals(t.Header, "Settings"));
                Layout();
                foreach (var section in Descendants(tabs).OfType<Expander>().ToArray()) section.IsExpanded = true;
                Layout();
                var status = Descendants(host).OfType<TextBlock>().Single(text =>
                    BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == "SessionStatus");
                var summary = Descendants(host).OfType<TextBlock>().Single(text =>
                    BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == "Readiness");
                var acquisition = Toggle("Enable acquisition");
                var calibration = Toggle("Recalibrate before guiding");
                if (!Descendants(host).OfType<TextBlock>().Any(text => text.Text == "Recalibrate before guiding"))
                    throw new InvalidOperationException("Guide calibration needs a visible label in the NINA toggle theme.");
                var guiding = Descendants(host).OfType<ComboBox>().Single(control =>
                    System.Windows.Automation.AutomationProperties.GetName(control) == "Guiding");
                var originalGuiding = guiding.SelectedValue;
                calibration.IsChecked = true;
                Layout();
                var options = exportedType.GetProperty("Options")!.GetValue(session)!;
                if (!(bool)options.GetType().GetProperty("ForceGuideCalibration")!.GetValue(options)! || !calibration.IsEnabled)
                    throw new InvalidOperationException("Guide calibration checkbox did not update the session settings.");
                guiding.SelectedValue = Enum.Parse(originalGuiding.GetType(), "Sequence");
                Layout();
                if (calibration.IsEnabled) throw new InvalidOperationException("Director calibration remained enabled with custom guiding instructions.");
                guiding.SelectedValue = originalGuiding;
                calibration.IsChecked = false;
                Layout();
                if (Descendants(host).OfType<CheckBox>().Any(control =>
                    System.Windows.Automation.AutomationProperties.GetName(control) is "Local target scheduling" or "Automatic workloads" or "I will supervise this attended run"))
                    throw new InvalidOperationException("Obsolete Director controls are still present in the session template.");
                if (!status.Text.Contains("Acquisition is off")) throw new InvalidOperationException("Initial session status did not explain disabled acquisition.");
                acquisition.IsChecked = true;
                Layout();
                if (!status.Text.Contains("when this session starts") || !summary.Text.Contains("Director selects targets")
                    || !summary.Text.Contains("gets the latest plans from PSF Guard") || summary.Text.Contains("Before New Target"))
                    throw new InvalidOperationException("Session header did not follow the bound acquisition and workload settings.");
                var root = new NINA.Sequencer.Container.SequenceRootContainer();
                var item = (NINA.Sequencer.SequenceItem.ISequenceItem)session;
                root.Add(item);
                root.Status = NINA.Core.Enum.SequenceEntityStatus.RUNNING;
                Layout();
                if (!status.Text.Contains("waiting for its turn"))
                    throw new InvalidOperationException("Session header did not reflect the running sequence.");
                root.Status = NINA.Core.Enum.SequenceEntityStatus.CREATED;
                root.Remove(item);
                acquisition.IsChecked = false;
                Layout();
                if (!status.Text.Contains("Acquisition is off")) throw new InvalidOperationException("Session header did not follow acquisition being turned off.");

                var targetFocus = Toggle("Autofocus when starting a target");
                var filterFocus = Toggle("Autofocus after a filter change");
                if (targetFocus.IsChecked != true || filterFocus.IsChecked != true)
                    throw new InvalidOperationException("Autofocus defaults were not preserved.");
                targetFocus.IsChecked = false;
                filterFocus.IsChecked = false;
                Layout();
                if ((bool)options.GetType().GetProperty("AutofocusOnTargetStart")!.GetValue(options)!
                    || (bool)options.GetType().GetProperty("AutofocusOnFilterChange")!.GetValue(options)!)
                    throw new InvalidOperationException("Autofocus switches did not update the session.");
                var focusOwner = Descendants(host).OfType<ComboBox>().Single(control => System.Windows.Automation.AutomationProperties.GetName(control) == "Autofocus");
                var originalFocus = focusOwner.SelectedValue;
                focusOwner.SelectedValue = Enum.Parse(originalFocus.GetType(), "Sequence");
                Layout();
                if (targetFocus.IsEnabled || filterFocus.IsEnabled) throw new InvalidOperationException("Director focus switches must defer to custom sequence ownership.");
                focusOwner.SelectedValue = originalFocus;
                targetFocus.IsChecked = filterFocus.IsChecked = true;

                CheckBox Toggle(string name) => Descendants(host).OfType<CheckBox>().Single(control =>
                    System.Windows.Automation.AutomationProperties.GetName(control) == name);
            }
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => Equals(t.Header, "Overview"));
            Layout();
            var reportButton = Descendants(host).OfType<Button>().SingleOrDefault(button => Equals(button.Content, "Report equipment"))
                ?? throw new InvalidOperationException("Director equipment report button was not rendered.");
            if (reportButton.Command is null || reportButton.IsEnabled || reportButton.Style != Application.Current.FindResource("StandardButton")
                || reportButton.ActualWidth < 80)
                throw new InvalidOperationException("Equipment report button lost its native style, binding or idle-service guard.");
            for (var tab = 0; tab < tabs.Items.Count; tab++)
            {
                tabs.SelectedIndex = tab;
                Layout();
                if (Equals(((TabItem)tabs.SelectedItem).Header, "Settings"))
                {
                    foreach (var section in Descendants(tabs).OfType<Expander>().ToArray()) section.IsExpanded = true;
                    Layout();
                }
                var controls = Descendants(tabs).OfType<Control>()
                    .Where(control => !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(control))).ToArray();
                if (Equals(((TabItem)tabs.SelectedItem).Header, "Settings"))
                {
                    if (controls.OfType<ComboBox>().Count() != 14 || controls.OfType<TextBox>().Count() != 19
                        || controls.OfType<CheckBox>().Count() != 11)
                        throw new InvalidOperationException($"Director session settings did not render every editor: {controls.OfType<ComboBox>().Count()} policies, {controls.OfType<TextBox>().Count()} numbers, {controls.OfType<CheckBox>().Count()} toggles.");
                    foreach (var combo in controls.OfType<ComboBox>())
                        if (combo.SelectedItem is null) throw new InvalidOperationException("Director policy selection is empty.");
                }
                if (Equals(((TabItem)tabs.SelectedItem).Header, "Instructions") && Descendants(host).OfType<Expander>().Count(expander => expander.DataContext is NINA.Sequencer.Container.SequentialContainer) < 7)
                    throw new InvalidOperationException("Director did not render all seven native instruction editors.");
                if (tab == 5 && Descendants(host).OfType<DataGrid>().Single().Columns.Any(column => column.ActualWidth < 65))
                    throw new InvalidOperationException("Activity columns collapsed below readable widths.");
                if (Equals(((TabItem)tabs.SelectedItem).Header, "Triggers and conditions"))
                {
                    // The MiniSequencer templates only render progress. Verify
                    // the native Advanced Sequencer fields are actually editable.
                    var triggerInput = Descendants(tabs).OfType<TextBox>().FirstOrDefault(field =>
                        ReferenceEquals(field.DataContext, editorTrigger) && BindingOperations.GetBinding(field, TextBox.TextProperty)?.Path.Path == "AfterExposures")
                        ?? throw new InvalidOperationException("NINA's trigger-specific editor was replaced by a generic summary.");
                    triggerInput.SetCurrentValue(TextBox.TextProperty, "4");
                    triggerInput.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                    if (editorTrigger.AfterExposures != 4) throw new InvalidOperationException("The trigger editor did not update its setting.");
                    if (!Descendants(tabs).OfType<TextBox>().Any(field => ReferenceEquals(field.DataContext, editorCondition)))
                        throw new InvalidOperationException("NINA's loop condition settings were not rendered.");
                }
                foreach (var field in controls.Where(c => c is TextBox or ComboBox or CheckBox))
                {
                    var rect = field.TransformToAncestor(host).TransformBounds(new Rect(field.RenderSize));
                    if (rect.Left < 0 || rect.Right > width + 1) throw new InvalidOperationException("Director editor escaped its layout.");
                }
                if (Equals(((TabItem)tabs.SelectedItem).Header, "Settings"))
                {
                    foreach (var section in Descendants(tabs).OfType<Expander>().ToArray()) section.IsExpanded = Equals(section.Header, "Safety and visibility") || Equals(section.Header, "Autofocus");
                    Layout();
                }
                var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                if (pixels.Where((_, index) => index % 4 != 3).Distinct().Count() < 8)
                    throw new InvalidOperationException("Director session rendered a blank surface.");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"session-{width}-{tab}.png"));
                encoder.Save(file);
            }

            void Layout()
            {
                host.Measure(new Size(width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                host.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                host.UpdateLayout();
            }
        }
    }).Task;

    private static async Task VerifyGlobalTriggerStatusAsync()
    {
        var (session, trigger) = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var root = new SequenceRootContainer();
            var session = new DirectorSessionContainer { Status = NINA.Core.Enum.SequenceEntityStatus.RUNNING };
            root.Add(session);
            var trigger = new SilentGlobalTrigger { Name = "Injected autofocus (simulator)" };
            root.Add(trigger);
            session.UpdateDisplay(d => d with { Phase = "Acquiring", Target = "Simulator", Operation = "HA · 120 s exposure" });
            return (session, trigger);
        });
        using var observation = new NinaTriggerActivity(session);
        using var progress = new PsfGuard.Director.Plugin.Acquisition.DirectorSessionProgress(session, new Progress<NINA.Core.Model.ApplicationStatus>());
        var running = Task.Run(() => trigger.Run(session, progress, CancellationToken.None));
        try
        {
            await WaitFor(() => session.TelemetrySnapshot().Operation == trigger.Name);
            if (!session.SessionStatus.Contains("Injected autofocus")) throw new InvalidOperationException("Global trigger status did not reach the session display.");
        }
        finally { trigger.Complete.TrySetResult(); await running; }
        await WaitFor(() => !session.IsTriggerRunning);
        if (session.CurrentDisplay.Operation != "HA · 120 s exposure") throw new InvalidOperationException("Finished global trigger left a stale status.");

        static async Task WaitFor(Func<bool> ready)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!ready()) await Task.Delay(50, deadline.Token);
        }
    }

    private sealed class SilentGlobalTrigger : NINA.Sequencer.Trigger.SequenceTrigger
    {
        internal TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override object Clone() => new SilentGlobalTrigger { Name = Name };
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => true;
        public override Task Execute(ISequenceContainer context, IProgress<NINA.Core.Model.ApplicationStatus> progress, CancellationToken token) => Complete.Task.WaitAsync(token);
    }

    private static IEnumerable<DataTemplate> Templates(ResourceDictionary dictionary)
    {
        foreach (var key in dictionary.Keys)
            if (key is DataTemplateKey && dictionary[key] is DataTemplate template) yield return template;
        foreach (var nested in dictionary.MergedDictionaries)
            foreach (var template in Templates(nested)) yield return template;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
