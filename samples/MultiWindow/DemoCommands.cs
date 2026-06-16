using System.Linq;
using Ryn.Core;
using Ryn.Ipc;

namespace MultiWindow;

/// <summary>
/// IPC commands the startup page calls so that opening the C#-side window and arranging the windows run on the
/// IPC dispatch path. Both forward to <see cref="IRynWindowManager"/> — the same window-manager a background
/// thread would use, but invoked while the event loop is actively servicing the request.
/// </summary>
#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class DemoCommands(IRynWindowManager windows)
#pragma warning restore CA1812
{
    /// <summary>Opens the third window from C# and returns its id.</summary>
    [RynCommand("demo.openFromCSharp")]
    public int OpenFromCSharp() =>
        windows.OpenWindow(new RynWindowOptions
        {
            Title = "Opened from C#",
            Width = 420,
            Height = 320,
            Html = Demo.ChildPage("Opened from C#", "#f59e0b"),
        }).Id;

    /// <summary>Tiles the open windows so all three are visible at once instead of stacked.</summary>
    [RynCommand("demo.tile")]
    public void Tile()
    {
        var ordered = windows.Windows.OrderBy(w => w.Id).ToList();
        var spots = new[] { (60, 80), (660, 80), (660, 460) };
        for (var i = 0; i < ordered.Count && i < spots.Length; i++)
            ordered[i].Move(spots[i].Item1, spots[i].Item2);
    }
}
