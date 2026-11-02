using VectorForge;

static class ValidFixture
{
    public static void Write(string path, ExportKind kind)
    {
        if (kind == ExportKind.Pdf)
            File.WriteAllText(path, "%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\nxref\n0 2\n0000000000 65535 f \n0000000009 00000 n \ntrailer\n<< /Size 2 /Root 1 0 R >>\nstartxref\n45\n%%EOF\n");
        else
        {
            using var bitmap = new System.Drawing.Bitmap(16, 16);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.Orange);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
        }
    }
}
