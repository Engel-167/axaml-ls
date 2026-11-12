using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SampleApp.Views;

/// <summary>Things set from code that XAML refers to by name: a style class, a resource key, a pseudo-class.</summary>
public static class CodeStyles
{
    public static void Apply(Control control, Application app)
    {
        control.Classes.Add("Card");
        app.Resources["FromCode"] = Brushes.Red;
        ((Avalonia.Controls.Primitives.TemplatedControl)control).PseudoClasses.Set(":custom", true);
    }
}
