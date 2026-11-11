using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Attach;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Operations on views and panels: pick, project, overlay, view and layout.</summary>
internal sealed partial class ScopeEndpoint
{
    private OneGame.UI.Rect ViewRect(int index)
    {
        _shell.View(index);
        if (_game.Ui.Find(ScopeStage.ViewId(index)) is not { IsEffectivelyVisible: true } element)
        {
            throw new ControlException("rejected", $"View {index} is not on screen; show it (view, layout) and capture a frame so it is laid out.");
        }

        return ViewPicking.Round(element.AbsoluteBounds);
    }

    private string PickOp(Args a)
    {
        RequireSources();
        double x = a.Double("x") ?? throw new ControlException("bad_args", "Argument 'x' is required (window pixels).");
        double y = a.Double("y") ?? throw new ControlException("bad_args", "Argument 'y' is required (window pixels).");
        int? given = a.Int("view");
        int index = -1;
        if (given is int g)
        {
            index = g;
        }
        else
        {
            for (int i = 0; i < _shell.Views.Count && index < 0; i++)
            {
                if (_game.Ui.Find(ScopeStage.ViewId(i)) is { IsEffectivelyVisible: true } e && ViewPicking.Round(e.AbsoluteBounds).Contains((float)x, (float)y))
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                throw new ControlException("not_found", $"No scene view is under window pixel ({x}, {y}).");
            }
        }

        ViewState view = _shell.View(index);
        ViewPicking.Hit hit = ViewPicking.Pick(_shell, view, ViewRect(index), (float)x, (float)y);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("view", index);
            w.WriteNumber("source", view.Source);
            if (hit.Entity.IsSome)
            {
                w.WriteNumber("entity", hit.Entity.Value);
            }
            else
            {
                w.WriteNull("entity");
            }

            w.WriteStartArray("point");
            w.WriteNumberValue(hit.Point.X);
            w.WriteNumberValue(hit.Point.Y);
            w.WriteNumberValue(hit.Point.Z);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string ProjectOp(Args a)
    {
        RequireSources();
        long raw = a.RequiredLong("entity");
        int index = a.Int("view") ?? -1;
        if (index < 0)
        {
            int wanted = SourceArg(a).Index;
            for (int i = 0; i < _shell.Views.Count && index < 0; i++)
            {
                index = _shell.Views[i].Source == wanted ? i : -1;
            }

            if (index < 0)
            {
                throw new ControlException("not_found", $"No view shows source {wanted}.");
            }
        }

        ViewState view = _shell.View(index);
        OneGame.UI.Rect rect = ViewRect(index);
        if (raw <= 0 || !ViewPicking.Project(_shell, view, rect, new EntityId((ulong)raw), out Vector2 screen, out Vector3 world))
        {
            throw new ControlException("not_found", $"View {index} (source {view.Source}) draws nothing for entity {raw} at step {_shell.Source(view.Source).History.CurrentStep}; check the entity and that its overlay is on.");
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("view", index);
            w.WriteNumber("entity", raw);
            w.WriteNumber("x", Math.Round(screen.X, 2));
            w.WriteNumber("y", Math.Round(screen.Y, 2));
            w.WriteBoolean("onScreen", rect.Contains(screen.X, screen.Y));
            w.WriteStartArray("world");
            w.WriteNumberValue(world.X);
            w.WriteNumberValue(world.Y);
            w.WriteNumberValue(world.Z);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string Overlay(Args a)
    {
        RequireSources();
        int viewIndex = a.Int("view") ?? _shell.ActiveView;
        ViewState view = _shell.View(viewIndex);
        SourceInfo info = _shell.Source(view.Source).Info;
        string? which = a.NameOrNumber("overlay");
        if (which is not null)
        {
            OverlayInfo? found = null;
            foreach (OverlayInfo o in info.Overlays)
            {
                if (string.Equals(o.Name, which, StringComparison.OrdinalIgnoreCase) || o.Id.ToString(CultureInfo.InvariantCulture) == which)
                {
                    found = o;
                    break;
                }
            }

            if (found is null)
            {
                throw new ControlException("not_found", $"Source {view.Source} has no overlay '{which}'; it declares {string.Join(", ", info.Overlays.Select(o => $"{o.Id}={o.Name}"))}.");
            }

            bool on = a.Bool("on") ?? ((view.Overlays & (1UL << found.Value.Id)) == 0);
            view.Overlays = on ? view.Overlays | (1UL << found.Value.Id) : view.Overlays & ~(1UL << found.Value.Id);
        }

        if (a.Bool("wireframe") is bool wire)
        {
            view.Wireframe = wire;
        }

        if (a.Bool("grid") is bool grid)
        {
            view.Grid = grid;
        }

        view.Touch();
        _shell.Touch();
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("view", viewIndex);
            w.WriteString("kind", view.Kind == ViewKind.Space3D ? "3d" : "2d");
            w.WriteBoolean("wireframe", view.Wireframe);
            w.WriteBoolean("grid", view.Grid);
            w.WriteStartArray("overlays");
            foreach (OverlayInfo o in info.Overlays)
            {
                w.WriteStartObject();
                w.WriteNumber("id", o.Id);
                w.WriteString("name", o.Name);
                w.WriteBoolean("on", (uint)o.Id < 64 && (view.Overlays & (1UL << o.Id)) != 0);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string ViewOp(Args a)
    {
        RequireSources();
        if (a.String("add") is string add)
        {
            ViewKind kind = add switch
            {
                "2d" => ViewKind.Plane2D,
                "3d" => ViewKind.Space3D,
                _ => throw new ControlException("bad_args", "Argument 'add' must be \"2d\" or \"3d\"."),
            };
            if (_shell.Views.Count >= 4)
            {
                throw new ControlException("rejected", "The shell shows at most 4 views.");
            }

            int src = a.Int("source") ?? _shell.ActiveSource;
            SourceSession s = _shell.Source(src);
            var view = new ViewState { Source = src, Kind = kind, Overlays = ShellModel.DefaultOverlays(s.Info) };
            _shell.AddView(view);
            _shell.FrameView(view);
            _shell.Touch();
        }
        else if (a.Has("index"))
        {
            int index = a.Int("index") ?? 0;
            ViewState view = _shell.View(index);
            if (a.Bool("remove") == true)
            {
                if (_shell.Views.Count == 1)
                {
                    throw new ControlException("rejected", "The last view can't be removed.");
                }

                _shell.RemoveView(index);
                _shell.ActiveView = Math.Min(_shell.ActiveView, _shell.Views.Count - 1);
            }
            else
            {
                if (a.Int("source") is int src)
                {
                    _shell.Source(src);
                    view.Source = src;
                }

                if (a.Vector("target") is Vector3 target)
                {
                    view.Target = target;
                }

                if (a.Double("halfHeight") is double hh)
                {
                    view.HalfHeight = (float)Math.Max(hh, 1e-6);
                }

                if (a.Double("distance") is double dist)
                {
                    view.Distance = (float)Math.Max(dist, 1e-6);
                }

                if (a.Double("yaw") is double yaw)
                {
                    view.Yaw = (float)yaw;
                }

                if (a.Double("pitch") is double pitch)
                {
                    view.Pitch = (float)Math.Clamp(pitch, -89, 89);
                }

                if (a.Bool("wireframe") is bool wire)
                {
                    view.Wireframe = wire;
                }

                if (a.Bool("grid") is bool grid)
                {
                    view.Grid = grid;
                }

                if (a.Bool("frame") == true)
                {
                    _shell.FrameView(view);
                }

                if (a.Bool("frameSelection") == true)
                {
                    _shell.FrameView(view, selection: true);
                }

                if (a.Bool("active") == true)
                {
                    _shell.ActiveView = index;
                }

                view.Touch();
                _shell.Touch();
            }
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("active", _shell.ActiveView);
            w.WriteStartArray("views");
            for (int i = 0; i < _shell.Views.Count; i++)
            {
                ViewState v = _shell.Views[i];
                w.WriteStartObject();
                w.WriteNumber("index", i);
                w.WriteString("kind", v.Kind == ViewKind.Space3D ? "3d" : "2d");
                w.WriteNumber("source", v.Source);
                w.WriteBoolean("wireframe", v.Wireframe);
                w.WriteBoolean("grid", v.Grid);
                w.WritePropertyName("target");
                w.WriteStartArray();
                w.WriteNumberValue(v.Target.X);
                w.WriteNumberValue(v.Target.Y);
                w.WriteNumberValue(v.Target.Z);
                w.WriteEndArray();
                if (v.Kind == ViewKind.Plane2D)
                {
                    w.WriteNumber("halfHeight", v.HalfHeight);
                }
                else
                {
                    w.WriteNumber("distance", v.Distance);
                    w.WriteNumber("yaw", v.Yaw);
                    w.WriteNumber("pitch", v.Pitch);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string Layout(Args a)
    {
        PanelState p = _shell.Panels;
        p.Entities = a.Bool("entities") ?? p.Entities;
        p.Inspector = a.Bool("inspector") ?? p.Inspector;
        p.Timeline = a.Bool("timeline") ?? p.Timeline;
        p.Log = a.Bool("log") ?? p.Log;
        _shell.Touch();
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("entities", p.Entities);
            w.WriteBoolean("inspector", p.Inspector);
            w.WriteBoolean("timeline", p.Timeline);
            w.WriteBoolean("log", p.Log);
            w.WriteEndObject();
        });
    }

    private void RequireSources()
    {
        if (_shell.Sources.Count == 0)
        {
            throw new ControlException("no_source", "No source is open; call open first.");
        }
    }

    private string TimelineOp(Args a)
    {
        RequireSources();
        if (a.String("fit") is string fit)
        {
            switch (fit)
            {
                case "all":
                    _shell.FitTimelineAll();
                    break;
                case "fork":
                    SourceSession s = SourceArg(a);
                    SourceSession fork = s.IsFork
                        ? s
                        : _shell.Sources.LastOrDefault(x => x.IsFork && ReferenceEquals(x.Root, s))
                            ?? throw new ControlException("not_found", $"Source {s.Index} has no fork to fit; fork it first.");
                    _shell.FitTimelineFork(fork);
                    break;
                default:
                    throw new ControlException("bad_args", "Argument 'fit' must be \"all\" or \"fork\".");
            }
        }
        else if (a.Has("from") || a.Has("to"))
        {
            TimeRange now = _shell.TimelineRange;
            _shell.SetTimelineWindow(a.Long("from") ?? now.Min, a.Long("to") ?? now.Max);
        }

        if (a.Double("zoom") is double zoom)
        {
            _shell.ZoomTimeline(zoom, a.Long("center") ?? Math.Clamp(_shell.Step, _shell.TimelineRange.Min, _shell.TimelineRange.Max));
        }

        if (a.Long("pan") is long pan)
        {
            _shell.PanTimeline(pan);
        }

        TimeRange r = _shell.TimelineRange;
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("from", r.Min);
            w.WriteNumber("to", r.Max);
            w.WriteBoolean("zoomed", _shell.TimelineZoomed);
            w.WriteNumber("axisFrom", _shell.MinSteps);
            w.WriteNumber("axisTo", _shell.MaxSteps);
            w.WriteNumber("step", _shell.Step);
            w.WriteEndObject();
        });
    }
}
