"""Encodes recorded frames into docs/media: an MP4 for the website and a GIF for the README.

Usage: python3 tools/Recordings/encode.py <frames folder> <clip> <output name> [--gif-width 960] [--gif-fps 15]

Needs ffmpeg with libx264 and freetype on PATH (used only here, never shipped). The caption of each segment is drawn on a bar
under the shell's frame, set in the Noto Sans file the app bundles.
"""
import json
import os
import subprocess
import sys
import tempfile
import textwrap

BAR = 84
FONT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "OneScope.App", "fonts", "NotoSans-Regular.ttf")


def main():
    args = sys.argv[1:]
    gif_width, gif_fps = 960, 15
    if "--gif-width" in args:
        i = args.index("--gif-width")
        gif_width = int(args[i + 1])
        del args[i:i + 2]
    if "--gif-fps" in args:
        i = args.index("--gif-fps")
        gif_fps = int(args[i + 1])
        del args[i:i + 2]
    frames, clip, name = args
    src = os.path.join(frames, clip)
    meta = json.load(open(os.path.join(src, "captions.json")))
    n = meta["frames"]
    tmp = tempfile.mkdtemp()
    filters = []
    caps = meta["captions"]
    for i, (start, text) in enumerate(caps):
        end = caps[i + 1][0] - 1 if i + 1 < len(caps) else n - 1
        for j, line in enumerate(textwrap.wrap(text, 120)):
            path = os.path.join(tmp, f"c{i}_{j}.txt")
            with open(path, "w") as f:
                f.write(line)
            filters.append(
                f"drawtext=fontfile={FONT}:textfile={path}:fontsize=27:fontcolor=0xe8e8ee:x=24:y=h-{BAR}+12+{j * 36}:"
                f"enable='between(n,{start},{end})'")
    probe = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0",
                            os.path.join(src, "00000.png")], capture_output=True, text=True, check=True).stdout.strip().split(",")
    w, h = int(probe[0]), int(probe[1])
    vf = f"pad={w}:{h + BAR}:0:0:color=0x14161c," + ",".join(filters) + ",format=yuv420p"
    out = os.path.join("docs", "media")
    os.makedirs(out, exist_ok=True)
    mp4 = os.path.join(out, name + ".mp4")
    subprocess.run(["nice", "ffmpeg", "-v", "error", "-y", "-framerate", "30", "-i", os.path.join(src, "%05d.png"), "-vf", vf,
                    "-c:v", "libx264", "-preset", "slow", "-crf", "24", "-movflags", "+faststart", "-r", "30", mp4], check=True)
    gif = os.path.join(out, name + ".gif")
    gf = (f"fps={gif_fps},scale={gif_width}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=96:stats_mode=diff[p];"
          "[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle")
    subprocess.run(["nice", "ffmpeg", "-v", "error", "-y", "-i", mp4, "-vf", gf, "-loop", "0", gif], check=True)
    for f in (mp4, gif):
        print(f, os.path.getsize(f))


main()
