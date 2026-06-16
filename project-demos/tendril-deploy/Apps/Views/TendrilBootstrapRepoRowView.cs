namespace TendrilDeploy.Apps.Views;

internal sealed record TendrilBootstrapRepoEntry(Guid Id, string CloneUrl);

internal sealed class TendrilBootstrapRepoRowView : ViewBase
{
    private readonly Guid _rowId;
    private readonly IState<List<TendrilBootstrapRepoEntry>> _repos;

    public TendrilBootstrapRepoRowView(Guid rowId, IState<List<TendrilBootstrapRepoEntry>> repos)
    {
        _rowId = rowId;
        _repos = repos;
    }

    public override object? Build()
    {
        var cloneUrl = UseState(() =>
            _repos.Value.FirstOrDefault(r => r.Id == _rowId)?.CloneUrl ?? "");

        UseEffect(() =>
        {
            var u = cloneUrl.Value.Trim();
            _repos.Set(list =>
            {
                var ix = list.FindIndex(e => e.Id == _rowId);
                if (ix < 0)
                    return list;
                var cur = list[ix];
                var next = cur with { CloneUrl = u };
                if (cur == next)
                    return list;
                var copy = list.ToList();
                copy[ix] = next;
                return copy;
            });
        }, cloneUrl.ToTrigger());

        var moreThanOne = _repos.Value.Count > 1;

        // Label lives once on the parent step (TendrilDeployView); rows are only input + remove.
        return Layout.Horizontal().Gap(1).Center().Width(Size.Full())
            | (Layout.Vertical().Width(Size.Grow())
                | cloneUrl.ToTextInput()
                    .Prefix(Icons.Github)
                    .Placeholder("https://github.com/org/repository.git")
                    .Width(Size.Full()))
            | new Button().Icon(Icons.Trash2).Variant(ButtonVariant.Ghost).Large()
                .Tooltip("Remove this row")
                .OnClick(_ =>
                {
                    if (!moreThanOne)
                        cloneUrl.Set("");
                    else
                        _repos.Set(list => list.Where(e => e.Id != _rowId).ToList());

                    return ValueTask.CompletedTask;
                });
    }
}
