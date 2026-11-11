"""Renders the frames of the README and website recordings with the released onescope tool.

Usage (from the repo root, with the tool, the sample hosts and the test modules built; see README.md in this folder):

    python3 tools/Recordings/record.py <clip> [--every N]

Clips: physics, live, compare, nav, cli. Frames go to frames/<clip>/NNNNN.png (1920x996) with captions.json beside them;
encode.py turns them into the MP4 and the GIF. --every N captures only every Nth frame (a quick look while tuning).

Environment: ONESCOPE (the tool), ONEGAME_GPU_ADAPTER (the graphics device; the recordings use a discrete GPU),
OSREC_DATA (folder with showcase.oppr), OSREC_BIN (folder holding bin/<Project>/Release/net10.0 for the hosts and test modules).
"""
import json
import math
import os
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from oscope import Shell  # noqa: E402

SIZE = "1920x996"
DATA = os.environ.get("OSREC_DATA", "data")
BIN = os.environ.get("OSREC_BIN", "artifacts/bin")
EVERY = 1


def ease(t):
    """0..1 -> 0..1, slow at both ends."""
    return 0.5 - 0.5 * math.cos(math.pi * t)


class Clip:
    def __init__(self, name):
        self.name = name
        self.n = 0
        self.captions = []
        self.last = None

    def caption(self, text):
        self.captions.append([self.n, text])

    def grab(self, sh):
        """Captures one frame of the shell and counts it."""
        if self.n % EVERY == 0:
            self.last = sh.frame(self.name, self.n)
        self.n += 1
        return self.last

    def done(self, sh_dir):
        d = os.path.join(sh_dir, self.name)
        with open(os.path.join(d, "captions.json"), "w") as f:
            json.dump({"frames": self.n, "captions": self.captions, "renderer": self.last and self.last["renderer"]}, f)
        print(self.name, "frames", self.n, "renderer", self.last and self.last["renderer"])


def centre_of(sh, entity, view, x0, y0, reach=240):
    """Finds the window pixel at the middle of what a press would hit as <entity>, near (x0, y0); project gives a body's origin,
    which for stacked bodies can sit under its neighbour."""
    xs, ys = [], []
    for y in range(int(y0 - reach), int(y0 + 60), 8):
        for x in range(int(x0 - 100), int(x0 + 100), 8):
            if sh.call("pick", x=x, y=y, view=view)["entity"] == entity:
                xs.append(x)
                ys.append(y)
    if not xs:
        raise RuntimeError("no pixel hits the entity")
    return sum(xs) / len(xs), sum(ys) / len(ys)


def physics():
    clip = Clip("physics")
    with Shell(["open", os.path.join(DATA, "showcase.oppr")], size=SIZE, frames_dir="frames") as sh:
        sh.call("view", index=1, remove=True)
        sh.call("layout", entities=False, inspector=False, log=False)
        sh.call("view", index=0, target=[2.5, 1.8, 4.5], distance=19, yaw=28, pitch=24)
        total = 600
        overview = ([2.5, 1.8, 4.5], 19.0, 28.0, 24.0)
        close = ([0.3, 3.8, 0.5], 12.5, 12.0, 14.0)
        clip.caption("OnePhysics replay in onescope: scrub any step, back and forth. Contact normals and joint limits are on.")
        for f in range(total):
            s = round(1 + 89 * ease(1 - abs(1 - 2 * ((f / 300) % 1))))
            # From frame 320 the camera moves in on the door and the slider and the view goes wireframe; it eases back at 520.
            b = ease(min(1.0, max(0.0, (f - 320) / 50))) - ease(min(1.0, max(0.0, (f - 520) / 50)))
            if f == 320:
                clip.caption("Closer, in wireframe: the hinge, slider and cone limits and the contact normals show through the shapes.")
            if f == 370:
                sh.call("view", index=0, wireframe=True)
            if f == 520:
                sh.call("view", index=0, wireframe=False)
                clip.caption("Seeking lands on exactly the state that playing straight there gives, from any direction.")
            if 320 <= f <= 570:
                tg = [overview[0][i] + (close[0][i] - overview[0][i]) * b for i in range(3)]
                sh.call("view", index=0, target=tg, distance=overview[1] + (close[1] - overview[1]) * b,
                        yaw=overview[2] + (close[2] - overview[2]) * b, pitch=overview[3] + (close[3] - overview[3]) * b)
            sh.call("seek", step=s)
            clip.grab(sh)
        clip.done("frames")


def live():
    clip = Clip("live")
    home = os.path.dirname(os.path.abspath(BIN))
    sockdir = tempfile.mkdtemp(prefix="ossock", dir=home)
    os.chmod(sockdir, 0o700)
    env = dict(os.environ, ONESCOPE_SOCKET_DIR=sockdir)
    # The host steps every 100 ms; the recorder waits for each new step before it captures, so the viewer shows one step a frame.
    host = subprocess.Popen(
        ["dotnet", os.path.join(BIN, "PhysicsHost/Release/net10.0/PhysicsHost.dll"), "3d", "600", "100", os.path.join(home, "host.hashes")],
        stdout=subprocess.PIPE, text=True, env=env)
    pid = host.stdout.readline().split()[1]
    try:
        with Shell(["attach", pid], size=SIZE, frames_dir="frames", env=env) as sh:
            sh.call("view", index=1, remove=True)
            sh.call("layout", entities=False, inspector=False, log=False)
            sh.call("view", index=0, target=[0.5, 4.6, 0.5], distance=21, yaw=32, pitch=22)
            clip.caption("Attached to a running program through its probe. The viewer re-simulates locally and follows it live.")
            last = 0
            for _ in range(130):
                while True:
                    now = sh.call("sources")["step"]
                    if now > last:
                        break
                    time.sleep(0.005)
                last = now
                clip.grab(sh)
            cam = sh.call("view")["views"][0]
            sh.call("fork")
            sh.call("layout", inspector=True)
            sh.call("view", index=1, target=cam["target"], distance=cam["distance"], yaw=cam["yaw"], pitch=cam["pitch"])
            clip.caption("Fork from this frame. The original is never touched; the fork records its own replay.")
            for _ in range(18):
                clip.grab(sh)
            clip.caption("Drag a body in the fork: every move is a recorded poke and one step of the fork.")
            top = 72057598332895235  # the top box of the stack, Body 3
            p = sh.call("project", entity=str(top), view=1, source=1)
            x0, y0 = centre_of(sh, top, 1, p["x"], p["y"])
            sh.call("input_pointer", action="move", x=x0, y=y0)
            sh.call("input_pointer", action="down", x=x0, y=y0, button="left")
            for k in range(1, 31):
                t = ease(k / 30)
                sh.call("input_pointer", action="move", x=x0 + 50 * t, y=y0 - 5 * t)
                clip.grab(sh)
            sh.call("input_pointer", action="up", x=x0 + 50, y=y0 - 5)
            clip.caption("Release and keep stepping the fork. The original (left) stays exactly as it was.")
            for _ in range(100):
                sh.call("step", source=1, delta=1)
                clip.grab(sh)
            d = sh.call("diff", source=1)
            print(json.dumps(d))
            diff = d.get("difference")
            clip.caption("diff names where the fork first differs from the original: "
                         + (sh.call("inspect", entity=str(diff["entity"]), source=1)["name"].split(" (")[0] + ", " + diff["field"] if diff else "nothing")
                         + f", and the divergence step ({d['divergence']}) is exact.")
            for _ in range(40):
                clip.grab(sh)
    finally:
        host.terminate()
    clip.done("frames")


def compare():
    clip = Clip("compare")
    a_dll = os.path.join(BIN, "OneScope.TestModule/Release/net10.0/OneScope.TestModule.dll")
    b_dll = os.path.join(BIN, "OneScope.TestModule.RuleB/Release/net10.0/OneScope.TestModule.dll")
    replay = "testdata/test-2d-600.ostm"
    with Shell(["open", replay, "--module", a_dll], size=SIZE, frames_dir="frames") as sh:
        sh.call("open", path=replay, moduleAssembly=b_dll)
        sh.call("compare", kind="versions")
        sh.call("view", index=1, remove=True)  # the 3D view of the first source; the two 2D views stay side by side
        sh.call("layout", entities=False, inspector=False, log=False, compare=True)
        sh.call("seek", step=120)
        clip.caption("One replay, two builds of the same module, each in its own load context: A (left) and B (right). B changes one rule.")
        for f in range(150):
            sh.call("seek", step=120 + round(179 * ease(f / 149)))
            clip.grab(sh)
        sh.call("seek", step=299)
        clip.caption("Go to the first divergence. The agree track under the timeline is green until step 300 and red after it.")
        for _ in range(10):
            clip.grab(sh)
        r = sh.call("compare", goto="first")
        d = r["difference"]
        clip.caption(f"Step {r['step']}: the hashes differ. First difference: entity {d['entity']}, {d['field']}, {d['source']} in A and {d['other']} in B (units: mm).")
        for _ in range(40):
            clip.grab(sh)
        clip.caption("Step on: the Compare panel lists every differing field; picking a row selects that entity in both views.")
        for k in range(1, 61):
            sh.call("seek", step=300 + round(30 * ease(k / 60)))
            clip.grab(sh)
        sh.call("inspect", entity=str(r["rows"][2]["entity"]))
        for _ in range(60):
            clip.grab(sh)
    clip.done("frames")


STAGES = ["voxels", "filtered spans", "compact spans", "regions", "contours", "polygons", "detail mesh"]


def nav():
    clip = Clip("nav")
    total_build = 7 * 24
    with Shell(["open", "testdata/path-build.opbi"], size=SIZE, frames_dir="frames") as sh:
        sh.call("view", index=1, remove=True)
        sh.call("layout", inspector=False, log=False)
        sh.call("view", index=0, frame=True)
        f = 0
        for stage in range(7):
            clip.caption(f"Navigation mesh build, stepped along its stage axis: stage {stage + 1} of 7, {STAGES[stage]}.")
            sh.call("seek", axis="stage", position=stage)
            for _ in range(24):
                sh.call("view", index=0, yaw=20 + 50 * ease(f / total_build))
                f += 1
                clip.grab(sh)
    with Shell(["open", "testdata/path-2d.oprp"], size=SIZE, frames_dir="frames") as sh:
        views = sh.call("view")["views"]
        for v in reversed(views):
            if v["kind"] == "3d":
                sh.call("view", index=v["index"], remove=True)
        sh.call("layout", inspector=False)
        v = sh.call("view")["views"][0]
        sh.call("view", index=0, halfHeight=v["halfHeight"] * 0.45)
        clip.caption("A crowd of agents on the navigation mesh, on the timeline: orders, joins and leaves are events you can seek to.")
        for step in range(0, 120):
            sh.call("seek", step=step)
            clip.grab(sh)
        for _ in range(10):
            clip.grab(sh)
    with Shell(["open", "testdata/path-flow.opgr"], size=SIZE, frames_dir="frames") as sh:
        views = sh.call("view")["views"]
        for v in reversed(views):
            if v["kind"] == "3d":
                sh.call("view", index=v["index"], remove=True)
        sh.call("layout", inspector=False, log=False)
        v = sh.call("view")["views"][0]
        sh.call("view", index=0, halfHeight=v["halfHeight"] * 0.5)
        clip.caption("A flow-field search stepped by iteration: the settled cells and the flow directions grow outward from the goals.")
        for pos in range(0, 60):
            sh.call("seek", axis="iteration", position=pos)
            clip.grab(sh)
        for _ in range(15):
            clip.grab(sh)
    clip.done("frames")


def cli():
    """Runs the command line the way a script or an agent would, against a headless shell, and keeps each command with its real
    output and the pictures the capture commands wrote. cli_frames.py lays them out."""
    out = os.path.join("frames", "cli")
    os.makedirs(out, exist_ok=True)
    transcript = []
    with Shell(["open", "testdata/physics-3d.oppr"], size="1280x720", frames_dir=out) as sh:
        sock = sh.path
        tool = os.environ.get("ONESCOPE", "onescope")

        def run(*cmd):
            p = subprocess.run([tool, *cmd, "--socket", sock], capture_output=True, text=True, timeout=60)
            shown = ["onescope"] + [c if not c.startswith("{") else "'" + c + "'" for c in cmd] + ["--socket", "s.sock"]
            text = p.stdout.strip()
            transcript.append({"cmd": " ".join(shown), "out": text, "exit": p.returncode})
            return json.loads(text) if text.startswith("{") else text

        run("seek", '{"step":40}')
        ents = run("entities", '{"limit":6}')["entities"]
        top = next(e["id"] for e in ents if e["name"].startswith("Body 3"))
        run("capture_frame", '{"view":0,"size":"960x540","path":"original.png"}')
        f = run("fork")
        fork_view = next(v["index"] for v in sh.call("view")["views"] if v["source"] == f["source"])
        run("poke", json.dumps({"source": f["source"], "poke": "impulse", "args": {"target": top, "impulse": [0, 6, 0]}, "step": True},
                               separators=(",", ":")))
        run("step", json.dumps({"source": f["source"], "delta": 12}, separators=(",", ":")))
        run("diff", json.dumps({"source": f["source"]}, separators=(",", ":")))
        run("capture_frame", json.dumps({"view": fork_view, "size": "960x540", "path": "fork.png"}, separators=(",", ":")))
    with open(os.path.join(out, "transcript.json"), "w") as fh:
        json.dump(transcript, fh)
    print("cli commands", len(transcript))


if __name__ == "__main__":
    args = sys.argv[1:]
    if "--every" in args:
        i = args.index("--every")
        EVERY = int(args[i + 1])
        del args[i:i + 2]
    globals()[args[0]]()
