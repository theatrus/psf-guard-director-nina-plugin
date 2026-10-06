using System.Reflection;
using System.Runtime.ExceptionServices;
using NINA.Equipment.Interfaces.Mediator;

namespace PsfGuard.Director.Plugin.Acquisition;

// Bind only the known native action signatures. Constructor failures are not
// evidence that an older signature should be tried instead.
internal static class NinaCompatibility
{
    internal static T Create<T>(ISafetyMonitorMediator safety, params object[] arguments) where T : class
    {
        var withSafety = arguments.Append(safety).ToArray();
        var constructor = Find(typeof(T), withSafety) ?? Find(typeof(T), arguments)
            ?? throw new NotSupportedException($"Unsupported NINA constructor for {typeof(T).Name}.");
        var values = constructor.GetParameters().Length == withSafety.Length ? withSafety : arguments;
        try { return (T)constructor.Invoke(values); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static ConstructorInfo? Find(Type type, object[] values) => type.GetConstructors().SingleOrDefault(c =>
        c.GetParameters().Length == values.Length && c.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(values[i])).All(x => x));

    internal static bool HasProperty(object instance, string name) => instance.GetType().GetProperty(name) is not null;
    internal static object? Read(object instance, string name) => instance.GetType().GetProperty(name)?.GetValue(instance);
    internal static void SetOptional(object instance, string name, object? value)
    {
        var property = instance.GetType().GetProperty(name);
        if (property is not null) property.SetValue(instance, value);
    }

    internal static void ConfigureFilter(object item, object filter)
    {
        var property = item.GetType().GetProperty(HasProperty(item, "Xfilter") ? "Xfilter" : "Filter")
            ?? throw new NotSupportedException("Unsupported NINA filter action.");
        property.SetValue(item, property.Name == "Xfilter" ? Read(filter, "Position") : filter);
    }

    internal static bool FilterMatches(object item, int slot) => HasProperty(item, "Xfilter")
        ? Read(item, "ComboBoxText") is null
            && Equals(Read(item, "XfilterDefinition"), slot.ToString(System.Globalization.CultureInfo.InvariantCulture))
            && Equals(Read(item, "Xfilter"), slot)
        : Read(item, "Filter") is { } filter && Read(filter, "Position") is short position && position == slot;
}
