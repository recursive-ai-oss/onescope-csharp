using System.Numerics;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Links between sources and ghost overlays: what keeps a selection and a camera with a thing that moves from one source to another.</summary>
internal sealed partial class ShellModel
{
    private readonly List<EntityLink> _linkScratch = [];
    private readonly List<SourceLink> _links = [];
    private readonly DrawList _trackScratch = new();

    /// <summary>A transfer between two open sources, as the shell resolves the module's declarations.</summary>
    internal readonly record struct SourceLink(int FromSource, EntityId From, int ToSource, EntityId To, long Step);

    /// <summary>The transfers every open source declares, resolved to source numbers. A link whose other source is not open is left out.</summary>
    public IReadOnlyList<SourceLink> Links()
    {
        _links.Clear();
        foreach (SourceSession s in _sources)
        {
            if (s.Info.Ghost is not null)
            {
                continue;
            }

            s.History.World.CollectLinks(_linkScratch);
            foreach (EntityLink l in _linkScratch)
            {
                SourceSession? other = FindByLinkKey(l.Source, s);
                if (other is null)
                {
                    continue;
                }

                SourceLink link = l.Direction == LinkDirection.ContinuedBy
                    ? new SourceLink(s.Index, l.Entity, other.Index, l.Other, l.Step)
                    : new SourceLink(other.Index, l.Other, s.Index, l.Entity, l.Step);
                if (!_links.Contains(link))
                {
                    _links.Add(link);
                }
            }
        }

        return _links;
    }

    /// <summary>The open source other sources' links call <paramref name="key"/>: an opened file before a fork of it, the lowest number first.</summary>
    private SourceSession? FindByLinkKey(string key, SourceSession except)
    {
        SourceSession? best = null;
        foreach (SourceSession s in _sources)
        {
            if (s == except || s.Info.Ghost is not null || s.Info.EffectiveLinkKey != key)
            {
                continue;
            }

            if (best is null || (best.IsFork && !s.IsFork))
            {
                best = s;
            }
        }

        return best;
    }

    /// <summary>
    /// Moves the selection across a transfer when the shared cursor has crossed it: the entity that left one source is selected where it arrived, and
    /// back again when the cursor returns. The active source follows the selection. Views that track the selection are re-centred.
    /// </summary>
    public void FollowLinks()
    {
        if (_sources.Count < 2 || !Selected.IsSome || ActiveSource >= _sources.Count)
        {
            if (_sources.Count > 0)
            {
                CentreTrackingViews();
            }

            return;
        }

        for (int hops = 0; hops < _sources.Count; hops++)
        {
            bool moved = false;
            foreach (SourceLink l in Links())
            {
                if (ActiveSource == l.FromSource && Selected == l.From && Step >= l.Step)
                {
                    ActiveSource = l.ToSource;
                    Selected = l.To;
                    moved = true;
                    break;
                }

                if (ActiveSource == l.ToSource && Selected == l.To && Step < l.Step)
                {
                    ActiveSource = l.FromSource;
                    Selected = l.From;
                    moved = true;
                    break;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        CentreTrackingViews();
    }

    private void CentreTrackingViews()
    {
        foreach (ViewState view in _views)
        {
            if (!view.Track || !Selected.IsSome || ActiveSource >= _sources.Count)
            {
                continue;
            }

            SourceSession s = _sources[ActiveSource];
            if (view.Source != s.Index && s.Info.Dimensions == Source(view.Source).Info.Dimensions)
            {
                view.Source = s.Index;
                view.Overlays = DefaultOverlays(s.Info);
            }

            if (view.Source != s.Index)
            {
                continue;
            }

            // The entity's centre: the middle of everything drawn for it, so a triangle or a capsule does not pull the camera to one corner.
            _trackScratch.Clear();
            s.History.View.Draw(_trackScratch, new DrawRequest(ulong.MaxValue));
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            bool any = false;
            foreach (ref readonly DrawItem item in _trackScratch.Items)
            {
                if (item.Entity != Selected || item.Kind == DrawKind.Label)
                {
                    continue;
                }

                any = true;
                Grow(item.A, ref min, ref max);
                if (item.Kind is DrawKind.Line or DrawKind.Arrow or DrawKind.Capsule or DrawKind.Cylinder or DrawKind.Triangle)
                {
                    Grow(item.B, ref min, ref max);
                }

                if (item.Kind == DrawKind.Triangle)
                {
                    Grow(item.C, ref min, ref max);
                }
            }

            if (any)
            {
                Vector3 centre = (min + max) / 2;
                view.Target = view.Kind == ViewKind.Plane2D ? new Vector3(centre.X, centre.Y, 0) : centre;
            }

            view.Touch();
        }
    }

    private static void Grow(Vector3 p, ref Vector3 min, ref Vector3 max)
    {
        min = Vector3.Min(min, p);
        max = Vector3.Max(max, p);
    }

    /// <summary>The source a ghost belongs over: the latest real source the ghost's module opens (any module when the cache names none), else null.</summary>
    public SourceSession? HostFor(SourceSession ghost)
    {
        string module = ghost.Info.Ghost?.ModuleId ?? string.Empty;
        for (int i = _sources.Count - 1; i >= 0; i--)
        {
            SourceSession s = _sources[i];
            if (s != ghost && s.Info.Ghost is null && (module.Length == 0 || s.Info.ModuleId == module) && s.Info.Dimensions == ghost.Info.Dimensions)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>Draws a ghost over every view of its host source instead of giving it views of its own. Returns false when there is no host.</summary>
    public bool OverlayGhost(SourceSession ghost)
    {
        SourceSession? host = HostFor(ghost);
        if (host is null)
        {
            return false;
        }

        return AttachGhost(ghost, host);
    }

    /// <summary>Draws <paramref name="ghost"/> over every view of <paramref name="host"/>. Returns false when the host has no view.</summary>
    public bool AttachGhost(SourceSession ghost, SourceSession host)
    {
        bool any = false;
        foreach (ViewState v in _views)
        {
            if (v.Source == host.Index)
            {
                if (!v.Ghosts.Contains(ghost))
                {
                    v.Ghosts.Add(ghost);
                    v.Touch();
                }

                any = true;
            }
        }

        if (any)
        {
            AddLog(ghost.Index, "shell", $"ghost {ghost.Label} drawn over source {host.Index}");
        }

        return any;
    }

    /// <summary>Stops drawing a ghost over a view (all views when <paramref name="view"/> is null).</summary>
    public int DetachGhost(SourceSession ghost, int? view)
    {
        int n = 0;
        for (int i = 0; i < _views.Count; i++)
        {
            if ((view is null || view == i) && _views[i].Ghosts.Remove(ghost))
            {
                _views[i].Touch();
                n++;
            }
        }

        if (n > 0)
        {
            Touch();
        }

        return n;
    }

    /// <summary>Fills <paramref name="list"/> with what a view shows: its source, then the ghosts over it, which always draw all their poses.</summary>
    public static void DrawView(ViewState view, SourceSession source, DrawList list)
    {
        source.History.View.Draw(list, new DrawRequest(view.Overlays));
        foreach (SourceSession g in view.Ghosts)
        {
            g.History.View.Draw(list, new DrawRequest(ulong.MaxValue));
        }
    }

    // ---- what a saved layout keeps of these features -------------------------------------------------------------------------

    private static void WriteViewExtras(System.Text.Json.Utf8JsonWriter w, ViewState v)
    {
        if (v.Track)
        {
            w.WriteBoolean("track", true);
        }

        if (v.Ghosts.Count > 0)
        {
            w.WriteStartArray("ghosts");
            foreach (SourceSession g in v.Ghosts)
            {
                w.WriteNumberValue(g.Index);
            }

            w.WriteEndArray();
        }
    }

    private static void ReadViewExtras(System.Text.Json.JsonElement v, ViewState view)
    {
        view.Track = v.TryGetProperty("track", out System.Text.Json.JsonElement t) && t.ValueKind == System.Text.Json.JsonValueKind.True;
        if (v.TryGetProperty("ghosts", out System.Text.Json.JsonElement g) && g.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (System.Text.Json.JsonElement e in g.EnumerateArray())
            {
                if (e.ValueKind == System.Text.Json.JsonValueKind.Number && e.TryGetInt32(out int index) && index >= 0 && view.PendingGhosts.Count < 64)
                {
                    view.PendingGhosts.Add(index);
                }
            }
        }
    }

    /// <summary>Turns the ghost numbers a restored layout named into sources; numbers that are not ghosts are dropped.</summary>
    private void ResolveGhosts()
    {
        foreach (ViewState v in _views)
        {
            foreach (int index in v.PendingGhosts)
            {
                if (index < _sources.Count && _sources[index].Info.Ghost is not null && !v.Ghosts.Contains(_sources[index]))
                {
                    v.Ghosts.Add(_sources[index]);
                }
            }

            v.PendingGhosts.Clear();
        }
    }
}
