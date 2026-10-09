using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace MarkdownPreview
{
    /// <summary>
    /// 图片预览控件：GDI+ 解码，Zoom 适应窗口，支持动画 GIF，
    /// 并根据 EXIF Orientation 自动校正手机照片方向。
    /// </summary>
    public class ImagePreviewControl : Panel
    {
        private MemoryStream _stream;  // Image.FromStream 要求流在图片生命周期内保持存活
        private Image _image;
        private PictureBox _box;

        public ImagePreviewControl(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new ArgumentException("图片数据为空。");
            }

            BackColor = Color.FromArgb(250, 250, 250);
            Padding = new Padding(12);

            _stream = new MemoryStream(bytes);
            _image = Image.FromStream(_stream);
            ApplyExifOrientation(_image);

            _box = new PictureBox();
            _box.Image = _image;
            _box.SizeMode = PictureBoxSizeMode.Zoom;
            _box.Dock = DockStyle.Fill;
            _box.BackColor = Color.White;
            _box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_box);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_box != null) { _box.Image = null; _box.Dispose(); _box = null; }
                if (_image != null) { _image.Dispose(); _image = null; }
                if (_stream != null) { _stream.Dispose(); _stream = null; }
            }
            base.Dispose(disposing);
        }

        private static void ApplyExifOrientation(Image img)
        {
            try
            {
                const int PropertyTagOrientation = 0x0112;
                if (Array.IndexOf(img.PropertyIdList, PropertyTagOrientation) < 0) return;
                PropertyItem p = img.GetPropertyItem(PropertyTagOrientation);
                short o = BitConverter.ToInt16(p.Value, 0);
                switch (o)
                {
                    case 2: img.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                    case 3: img.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case 4: img.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
                    case 5: img.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                    case 6: img.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case 7: img.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                    case 8: img.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }
            }
            catch
            {
                // 无 EXIF 或方向值异常时保持原图
            }
        }
    }
}
