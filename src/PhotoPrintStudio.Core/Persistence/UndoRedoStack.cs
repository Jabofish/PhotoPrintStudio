namespace PhotoPrintStudio.Core.Persistence;

public sealed class UndoRedoStack<TState>
{
    private readonly Stack<TState> _undo = new();
    private readonly Stack<TState> _redo = new();
    private readonly int _maxDepth;

    public UndoRedoStack(int maxDepth = 60)
    {
        _maxDepth = maxDepth;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void PushUndo(TState snapshot)
    {
        _undo.Push(snapshot);
        _redo.Clear();

        if (_undo.Count > _maxDepth)
        {
            var items = _undo.ToArray(); // newest first
            _undo.Clear();
            for (int i = Math.Min(_maxDepth - 1, items.Length - 1); i >= 0; i--)
                _undo.Push(items[i]);
        }
    }

    public TState? Undo(TState current)
    {
        if (_undo.Count == 0) return default;
        _redo.Push(current);
        return _undo.Pop();
    }

    public TState? Redo(TState current)
    {
        if (_redo.Count == 0) return default;
        _undo.Push(current);
        return _redo.Pop();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
