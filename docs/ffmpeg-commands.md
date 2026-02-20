
## VIDEO (keep audio)

### Reels / Story (9:16) → 1080×1920
ffmpeg -y -i "Create_short_video_202602201555_t2f88.mp4" -vf "scale=1080:1920" -c:v libx264 -pix_fmt yuv420p -crf 23 -preset veryfast -c:a aac -b:a 128k "ig_reel_1080x1920.mp4"

### Feed video (4:5) → 1080×1350 (center crop, no distortion)
ffmpeg -y -i "Create_short_video_202602201555_t2f88.mp4" -vf "scale=1080:-2,crop=1080:1350" -c:v libx264 -pix_fmt yuv420p -crf 23 -preset veryfast -c:a aac -b:a 128k "ig_feed_1080x1350.mp4"

### Feed video (square 1:1) → 1080×1080 (center crop)
ffmpeg -y -i "Create_short_video_202602201555_t2f88.mp4" -vf "scale=-2:1080,crop=1080:1080" -c:v libx264 -pix_fmt yuv420p -crf 23 -preset veryfast -c:a aac -b:a 128k "ig_feed_1080x1080.mp4"

---

## IMAGES (file name: p_1)

### Square feed (1:1) → 1080×1080 (center crop)
ffmpeg -y -i "p_1.jpg" -vf "scale=-2:1080,crop=1080:1080" "p_1_ig_1080x1080.jpg"

### Portrait feed (4:5) → 1080×1350 (center crop)
ffmpeg -y -i "p_1.jpg" -vf "scale=1080:-2,crop=1080:1350" "p_1_ig_1080x1350.jpg"

### Landscape feed (1.91:1) → 1080×566 (center crop)
ffmpeg -y -i "p_1.jpg" -vf "scale=-2:566,crop=1080:566" "p_1_ig_1080x566.jpg"

### Story/Reels cover image (9:16) → 1080×1920 (center crop)
ffmpeg -y -i "p_1.jpg" -vf "scale=1080:-2,crop=1080:1920" "p_1_ig_1080x1920.jpg"

---

## Quick sanity check (optional)
ffprobe -hide_banner -i "ig_reel_1080x1920.mp4"