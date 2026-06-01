namespace Ryn.Core.Internal;

internal sealed class RynWindowAccessor
{
    private RynWindow? _window;
    private readonly List<Action<RynWindow>> _onReady = [];

    internal RynWindow? Window
    {
        get => _window;
        set
        {
            _window = value;
            if (value is not null && _onReady.Count > 0)
            {
                var pending = _onReady.ToArray();
                _onReady.Clear();
                foreach (var action in pending)
                    action(value);
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> now if the window already exists, otherwise once it is set.</summary>
    internal void OnReady(Action<RynWindow> action)
    {
        if (_window is not null)
            action(_window);
        else
            _onReady.Add(action);
    }
}
