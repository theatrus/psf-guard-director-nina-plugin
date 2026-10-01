using System.ComponentModel.Composition;
using System.Windows;

namespace PsfGuard.Director.Plugin.Sequencer;

[Export(typeof(ResourceDictionary))]
public partial class DirectorSessionTemplate : ResourceDictionary
{
    public DirectorSessionTemplate() => InitializeComponent();
}
