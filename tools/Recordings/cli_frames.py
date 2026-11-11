"""Lays out the command-line recording: a terminal with the real commands and their output on the left, the pictures the capture
commands wrote on the right. Reads frames/cli (from record.py cli) and writes docs/media/<name>.mp4 and .gif.

Usage: python3 tools/Recordings/cli_frames.py <frames folder> <output name>
Needs Pillow and ffmpeg (encoding only, never shipped); text is set in Noto Sans Mono and Noto Sans (SIL OFL).
"""
import json
import os
import re
import subprocess
import sys
import textwrap

from PIL import Image, ImageDraw, ImageFont

W, H, BAR = 1920, 1080, 84
FONT_SANS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "OneScope.App", "fonts", "NotoSans-Regular.ttf")
FONT_MONO = "/usr/share/fonts/truetype/noto/NotoSansMono-Regular.ttf"
CAPTION = ("The same operations a window or an MCP agent uses, from the command line against a headless shell. Output is shortened with "
           "an ellipsis; the two pictures are the files the capture commands wrote.")
LINE = 26
COLS = 68


def clean(text):
    """Drops directory parts from file names in results, so only file names show."""
    return re.sub(r'"(path)":"[^"]*/([^/"]+)"', r'"\1":"\2"', text)


def wrap(text, limit_lines):
    lines = textwrap.wrap(text, COLS, break_long_words=True, break_on_hyphens=False)
    if len(lines) > limit_lines:
        lines = lines[:limit_lines]
        lines[-1] = lines[-1][:COLS - 2] + " …"
    return lines


def main():
    folder, name = sys.argv[1:3]
    src = os.path.join(folder, "cli")
    steps = json.load(open(os.path.join(src, "transcript.json")))
    mono = ImageFont.truetype(FONT_MONO, 21)
    sans = ImageFont.truetype(FONT_SANS, 27)
    small = ImageFont.truetype(FONT_SANS, 22)
    imgs = {}
    for key in ("original", "fork"):
        im = Image.open(os.path.join(src, key + ".png")).convert("RGB")
        imgs[key] = im.resize((780, 439), Image.LANCZOS)
    # (frames to type the command, frames to hold after the output appears, picture that appears with the output)
    shows = {"capture_frame": None}
    cap_seen = 0
    timeline = []  # per command: lines of its output
    for st in steps:
        op = st["cmd"].split()[1]
        limit = 7 if op == "diff" else 3
        timeline.append((wrap("$ " + st["cmd"], 4), wrap(clean(st["out"]), limit), op))
    frames = []
    done = []  # lines already on screen
    pics = {"original": False, "fork": False}
    for lines_cmd, lines_out, op in timeline:
        text = " ".join(l[2:] if i == 0 else l for i, l in enumerate(lines_cmd))
        total = sum(len(l) for l in lines_cmd)
        typed_frames = max(14, total // 3)
        for k in range(typed_frames + 1):
            n = int(total * k / typed_frames)
            shown, left = [], n
            for l in lines_cmd:
                shown.append(l[:left])
                left = max(0, left - len(l))
                if left == 0:
                    break
            frames.append((list(done) + shown, dict(pics)))
        done += lines_cmd
        done += lines_out
        if op == "capture_frame":
            pics["original" if not pics["original"] else "fork"] = True
        hold = 26
        for _ in range(hold):
            frames.append((list(done), dict(pics)))
    for _ in range(36):
        frames.append((list(done), dict(pics)))

    out = os.path.join("docs", "media")
    os.makedirs(out, exist_ok=True)
    mp4 = os.path.join(out, name + ".mp4")
    ff = subprocess.Popen(["nice", "ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}", "-r", "30", "-i", "-",
                           "-vf", "format=yuv420p", "-c:v", "libx264", "-preset", "slow", "-crf", "22", "-movflags", "+faststart", mp4],
                          stdin=subprocess.PIPE)
    max_lines = (H - BAR - 40) // LINE
    for lines, p in frames:
        im = Image.new("RGB", (W, H), (0x14, 0x16, 0x1c))
        d = ImageDraw.Draw(im)
        d.rectangle([16, 16, 910, H - BAR - 16], fill=(0x0c, 0x0e, 0x12))
        vis = lines[-max_lines:]
        y = 28
        for l in vis:
            colour = (0x9c, 0xe3, 0x9c) if l.startswith("$") else (0xd7, 0xda, 0xe0) if True else None
            d.text((30, y), l, font=mono, fill=colour)
            y += LINE
        for i, key in enumerate(("original", "fork")):
            ox, oy = 1060, 30 + i * (439 + 66)
            d.text((ox, oy), "the original, step 40" if key == "original" else "the fork, 13 steps after the poke", font=small, fill=(0xe8, 0xe8, 0xee))
            if p[key]:
                im.paste(imgs[key], (ox, oy + 34))
            else:
                d.rectangle([ox, oy + 34, ox + 779, oy + 34 + 438], outline=(0x30, 0x34, 0x3e))
        wrapped = textwrap.wrap(CAPTION, 105)
        d.text((24, H - BAR + 12), "\n".join(wrapped), font=sans, fill=(0xe8, 0xe8, 0xee), spacing=6)
        ff.stdin.write(im.tobytes())
    ff.stdin.close()
    ff.wait()
    gif = os.path.join(out, name + ".gif")
    gf = ("fps=15,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=96:stats_mode=diff[p];"
          "[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle")
    subprocess.run(["nice", "ffmpeg", "-v", "error", "-y", "-i", mp4, "-vf", gf, "-loop", "0", gif], check=True)
    for f in (mp4, gif):
        print(f, os.path.getsize(f))


main()
