using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Moq;
using NINA.Profile.Interfaces;

namespace PsfGuard.Director.SimulatorProbe;

internal static partial class SessionUiProbe
{
    private static void RenderProfileDefaults(string directory, Assembly pluginAssembly)
    {
        var profile = new Mock<IProfile> { DefaultValue = DefaultValue.Mock };
        profile.SetupGet(p => p.Id).Returns(Guid.NewGuid());
        profile.SetupGet(p => p.Name).Returns("Isolated defaults test");
        profile.SetupGet(p => p.PluginSettings).Returns(new NINA.Profile.PluginSettings());
        var profiles = new Mock<IProfileService>();
        profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
        var pluginType = pluginAssembly.GetType("PsfGuard.Director.Plugin.DirectorPlugin")!;
        var plugin = Activator.CreateInstance(pluginType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [profiles.Object], null)!;
        var template = (DataTemplate)Application.Current.FindResource("PSF Guard Director_Options");
        var view = (FrameworkElement)template.LoadContent();
        view.DataContext = plugin;
        var host = new Border { Width = 640, Background = (Brush)Application.Current.FindResource("BackgroundBrush"), Child = view };
        System.Windows.Documents.TextElement.SetForeground(host, (Brush)Application.Current.FindResource("PrimaryBrush"));
        Layout();
        var defaults = Descendants(host).OfType<Expander>().Single(e => Equals(e.Header, "Defaults for new Director Sessions"));
        defaults.IsExpanded = true;
        Layout();
        foreach (var section in Descendants(defaults).OfType<Expander>().ToArray()) section.IsExpanded = true;
        Layout();
        var policies = Descendants(defaults).OfType<ComboBox>().ToArray();
        if (policies.Length != 15 || policies.Any(policy => policy.SelectedItem is null))
            throw new InvalidOperationException("Profile default policy dropdowns are missing their selected values.");
        foreach (var section in Descendants(defaults).OfType<Expander>().ToArray())
            section.IsExpanded = section.Header is "Autofocus" or "Meridian avoidance" or "Safety and visibility";
        Layout();
        var before = Descendants(defaults).OfType<TextBox>().Single(field => System.Windows.Automation.AutomationProperties.GetName(field) == "Minutes before transit");
        var after = Descendants(defaults).OfType<TextBox>().Single(field => System.Windows.Automation.AutomationProperties.GetName(field) == "Minutes after transit");
        before.Text = "60";
        after.Text = "0";
        Layout();
        var save = Descendants(defaults).OfType<Button>().Single(button => button.Content is TextBlock { Text: "Save profile defaults" });
        if (save.Command is null || !save.Command.CanExecute(null)) throw new InvalidOperationException("Profile defaults cannot be saved from the plugin page.");
        save.Command.Execute(null);
        Layout();
        var stored = new NINA.Profile.PluginOptionsAccessor(profiles.Object, new("03a1d13e-67eb-4e24-a407-82bce7e576a5"))
            .GetValueString("SessionDefaults", "");
        var json = Newtonsoft.Json.Linq.JObject.Parse(stored);
        if ((double)json["MeridianBeforeMinutes"]! != 60 || (double)json["MeridianAfterMinutes"]! != 0 || (bool)json["EnableAcquisition"]!)
            throw new InvalidOperationException("Plugin page did not save asymmetric meridian defaults without arming acquisition.");
        foreach (var field in Descendants(defaults).OfType<Control>().Where(c => c is TextBox or ComboBox or CheckBox))
        {
            var rect = field.TransformToAncestor(host).TransformBounds(new Rect(field.RenderSize));
            if (rect.Left < 0 || rect.Right > 641) throw new InvalidOperationException("Profile defaults editor escaped its layout.");
        }
        var bitmap = new RenderTargetBitmap(640, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, "profile-defaults-640.png"))) encoder.Save(file);
        File.WriteAllText(Path.Combine(directory, "profile-defaults-verified.json"), stored);

        void Layout()
        {
            host.Measure(new Size(640, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 640, host.DesiredSize.Height));
            host.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            host.UpdateLayout();
        }
    }
}
