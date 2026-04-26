"""Generate Scour app icon - magnifying glass over a sparkle/clean motif."""
from PIL import Image, ImageDraw, ImageFont
import math, struct, io, os

def draw_icon(size):
    """Draw the Scour icon at the given size."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    s = size / 256  # scale factor
    cx, cy = size / 2, size / 2

    # --- Background: rounded square with Catppuccin Mocha Base gradient feel ---
    pad = int(8 * s)
    radius = int(48 * s)
    # Dark base
    draw.rounded_rectangle([pad, pad, size - pad, size - pad], radius=radius, fill=(30, 30, 46, 255))
    # Subtle inner glow
    inner_pad = int(12 * s)
    draw.rounded_rectangle([inner_pad, inner_pad, size - inner_pad, size - inner_pad],
                          radius=radius - int(4*s), fill=(36, 36, 54, 255))

    # --- Magnifying glass ---
    # Glass circle
    glass_cx = cx - int(20 * s)
    glass_cy = cy - int(28 * s)
    glass_r = int(58 * s)
    glass_thick = int(10 * s)

    # Outer ring (Catppuccin Mauve: #cba6f7)
    for t in range(glass_thick):
        r = glass_r - t
        alpha = 255 if t < glass_thick - 2 else 180
        draw.ellipse([glass_cx - r, glass_cy - r, glass_cx + r, glass_cy + r],
                    outline=(203, 166, 247, alpha), width=2)

    # Glass fill (subtle tinted)
    draw.ellipse([glass_cx - glass_r + glass_thick, glass_cy - glass_r + glass_thick,
                  glass_cx + glass_r - glass_thick, glass_cy + glass_r - glass_thick],
                fill=(49, 50, 68, 200))

    # Glass shine arc
    shine_r = int(glass_r * 0.6)
    shine_thick = max(2, int(3 * s))
    draw.arc([glass_cx - shine_r, glass_cy - shine_r - int(4*s),
              glass_cx + shine_r - int(20*s), glass_cy + shine_r - int(24*s)],
             start=200, end=310, fill=(205, 214, 244, 120), width=shine_thick)

    # Handle
    handle_start_x = glass_cx + int(glass_r * 0.65)
    handle_start_y = glass_cy + int(glass_r * 0.65)
    handle_end_x = handle_start_x + int(60 * s)
    handle_end_y = handle_start_y + int(60 * s)
    handle_thick = int(14 * s)

    # Handle shadow
    draw.line([handle_start_x + int(2*s), handle_start_y + int(2*s),
               handle_end_x + int(2*s), handle_end_y + int(2*s)],
              fill=(17, 17, 27, 150), width=handle_thick + int(4*s))

    # Handle body (Catppuccin Peach: #fab387)
    draw.line([handle_start_x, handle_start_y, handle_end_x, handle_end_y],
              fill=(250, 179, 135, 255), width=handle_thick)

    # Handle cap
    cap_r = int(handle_thick * 0.7)
    draw.ellipse([handle_end_x - cap_r, handle_end_y - cap_r,
                  handle_end_x + cap_r, handle_end_y + cap_r],
                fill=(245, 194, 231, 255))  # Pink

    # --- Sparkles inside the glass (cleaning/scouring motif) ---
    sparkle_color = (249, 226, 175, 230)  # Catppuccin Yellow
    sparkle_positions = [
        (glass_cx - int(18*s), glass_cy - int(12*s), int(10*s)),
        (glass_cx + int(12*s), glass_cy - int(20*s), int(7*s)),
        (glass_cx - int(4*s), glass_cy + int(14*s), int(8*s)),
        (glass_cx + int(20*s), glass_cy + int(6*s), int(6*s)),
    ]

    for sx, sy, sr in sparkle_positions:
        _draw_sparkle(draw, sx, sy, sr, sparkle_color, s)

    # --- Small accent sparkle outside glass (top-right) ---
    _draw_sparkle(draw, int(cx + 60*s), int(cy - 70*s), int(12*s), (137, 180, 250, 200), s)  # Blue
    _draw_sparkle(draw, int(cx + 80*s), int(cy - 50*s), int(6*s), (166, 227, 161, 180), s)   # Green

    return img

def _draw_sparkle(draw, x, y, size, color, scale):
    """Draw a 4-pointed sparkle/star."""
    thick = max(1, int(2 * scale))
    # Vertical line
    draw.line([x, y - size, x, y + size], fill=color, width=thick)
    # Horizontal line
    draw.line([x - size, y, x + size, y], fill=color, width=thick)
    # Diagonal lines (shorter)
    ds = int(size * 0.55)
    draw.line([x - ds, y - ds, x + ds, y + ds], fill=(*color[:3], color[3] // 2), width=max(1, thick - 1))
    draw.line([x + ds, y - ds, x - ds, y + ds], fill=(*color[:3], color[3] // 2), width=max(1, thick - 1))
    # Center dot
    cd = max(1, int(2 * scale))
    draw.ellipse([x - cd, y - cd, x + cd, y + cd], fill=color)

def create_ico(output_path):
    """Create a multi-size ICO file."""
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = []

    # Generate at 256 and resize down for quality
    base = draw_icon(256)

    for sz in sizes:
        if sz == 256:
            images.append(base.copy())
        else:
            resized = base.resize((sz, sz), Image.LANCZOS)
            images.append(resized)

    # Save as ICO
    images[0].save(output_path, format='ICO', sizes=[(sz, sz) for sz in sizes],
                   append_images=images[1:])
    print(f"Icon saved to {output_path} ({os.path.getsize(output_path)} bytes)")

    # Also save a 256px PNG preview
    preview_path = output_path.replace('.ico', '_preview.png')
    base.save(preview_path, format='PNG')
    print(f"Preview saved to {preview_path}")

if __name__ == "__main__":
    out_dir = os.path.dirname(os.path.abspath(__file__))
    icon_path = os.path.join(os.path.dirname(out_dir), "src", "Scour.App", "scour.ico")
    create_ico(icon_path)
