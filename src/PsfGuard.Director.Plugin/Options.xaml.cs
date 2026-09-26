using System.ComponentModel.Composition;
using System.Windows;

namespace PsfGuard.Director.Plugin;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options() => InitializeComponent();
}
