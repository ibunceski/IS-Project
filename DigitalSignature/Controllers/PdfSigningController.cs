using Microsoft.AspNetCore.Mvc;
using QRCoder;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.IO.Image;
using iText.Layout.Properties;
using iText.Kernel.Geom;
using Path = System.IO.Path;
using iText.Kernel.Colors;
using iText.Kernel.Pdf.Canvas;
using iText.Layout.Borders;
using iText.Kernel.Font;
using iText.IO.Font;
using System.Drawing;
using System.Drawing.Imaging;

[ApiController]
[Route("api/[controller]")]
public class PdfSigningController : ControllerBase
{
    private readonly IWebHostEnvironment _env;

    public PdfSigningController(IWebHostEnvironment env)
    {
        _env = env;
    }

    private iText.Layout.Element.Image CreateQRCode(String publicUrl)
    {
        var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(publicUrl, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(qrCodeData);
        byte[] qrBytes = qrCode.GetGraphic(20);

        var logoPath = Path.Combine(_env.WebRootPath, "images", "finki_logo.png");
        if (!System.IO.File.Exists(logoPath))
            throw new Exception("Logo image not found.");

        using (var qrBitmap = new Bitmap(new MemoryStream(qrBytes)))
        {
            using (var compatibleQrBitmap = new Bitmap(qrBitmap.Width, qrBitmap.Height, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(compatibleQrBitmap))
            {
                graphics.DrawImage(qrBitmap, 0, 0);

                using (var logoBitmap = new Bitmap(logoPath))
                {
                    int logoSize = (int)(compatibleQrBitmap.Width * 0.2);
                    var resizedLogo = new Bitmap(logoBitmap, new Size(logoSize, logoSize));

                    int x = (compatibleQrBitmap.Width - logoSize) / 2;
                    int y = (compatibleQrBitmap.Height - logoSize) / 2;

                    graphics.DrawImage(resizedLogo, x, y);

                    using (var ms = new MemoryStream())
                    {
                        compatibleQrBitmap.Save(ms, ImageFormat.Png);
                        qrBytes = ms.ToArray();
                    }
                }
            }
        }

        var imgData = ImageDataFactory.Create(qrBytes);
        var qrImage = new iText.Layout.Element.Image(imgData).ScaleToFit(100, 100);
        return qrImage;
    }

    private Table CreateTable(String publicUrl, iText.Layout.Element.Image qrImage)
    {
        var table = new Table(UnitValue.CreatePercentArray(new float[] { 15, 70, 15 }))
    .UseAllAvailableWidth()
    .SetMarginTop(100)
    .SetMarginLeft(36)
    .SetMarginRight(36);

        var border = new SolidBorder(ColorConstants.BLACK, 1);

        // Left cell (Signature info)
        var leftContent = new Paragraph()
            .Add(new Text("Потпишува: \n"))
            .Add(new Text("Датум и време: \n"))
            .Add(new Text("Верификација: \n"))
            .SetFontSize(8);

        var leftCell = new Cell()
            .Add(leftContent)
            .SetPadding(5)
            .SetVerticalAlignment(VerticalAlignment.TOP)
            .SetBorder(border);

        // Middle cell (Text info)
        var middleContent = new Paragraph()
            .Add("Факултет за информатички науки и компјутерско инженерство\n")
            .Add($"{DateTime.Now:dd.MM.yyyy HH:mm}\n")
            .Add("Информации за верификација на автентичноста на овој документ се достапни со користење на кодот за верификација (QR-кодот) односно на линкот подолу.")
            .SetFontSize(8);

        var middleCell = new Cell()
            .Add(middleContent)
            .SetPadding(5)
            .SetVerticalAlignment(VerticalAlignment.TOP)
            .SetBorder(border);

        // Right cell (QR image)
        var rightCell = new Cell()
            .Add(qrImage)
            .SetPadding(5)
            .SetVerticalAlignment(VerticalAlignment.MIDDLE)
            .SetHorizontalAlignment(HorizontalAlignment.CENTER)
            .SetBorder(border);

        // Add the first row
        table.AddCell(leftCell);
        table.AddCell(middleCell);
        table.AddCell(rightCell);

        // Verification URL row
        var verifyUrl = new Paragraph()
            .Add(publicUrl)
            .SetFontSize(8);

        var verifyUrlCell = new Cell(1, 3)
            .Add(verifyUrl)
            .SetFontColor(ColorConstants.BLUE)
            .SetVerticalAlignment(VerticalAlignment.MIDDLE)
            .SetHorizontalAlignment(HorizontalAlignment.CENTER)
            .SetBorder(border)
            .SetTextAlignment(TextAlignment.CENTER);

        table.AddCell(verifyUrlCell);

        // Verification text row
        var verifyText = new Paragraph()
            .Add("Овој документ е официјално потпишан со електронски печат и електронски временски жиг. Автентичноста на печатените копии од овој документ можат да бидат електронски верификувани.")
            .SetFontSize(8);

        var verifyTextCell = new Cell(1, 3)
            .Add(verifyText)
            .SetVerticalAlignment(VerticalAlignment.MIDDLE)
            .SetHorizontalAlignment(HorizontalAlignment.CENTER)
            .SetBorder(border)
            .SetTextAlignment(TextAlignment.CENTER);

        table.AddCell(verifyTextCell);
        return table;
    }

    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0 || !file.FileName.EndsWith(".pdf"))
            return BadRequest("Please upload a valid PDF.");

        var uploadsPath = Path.Combine(_env.WebRootPath, "signed");
        Directory.CreateDirectory(uploadsPath);

        var originalFileName = Guid.NewGuid() + ".pdf";
        var originalFilePath = Path.Combine(uploadsPath, originalFileName);

        using (var stream = new FileStream(originalFilePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var signedFileName = "signed_" + originalFileName;
        var signedFilePath = Path.Combine(uploadsPath, signedFileName);
        var publicUrl = $"{Request.Scheme}://{Request.Host}/signed/{signedFileName}";

        var qrImage = CreateQRCode(publicUrl);

        // Register Cyrillic font
        var fontPath = Path.Combine(_env.WebRootPath, "fonts", "NotoSans-Regular.ttf");
        var font = PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);

        using (var pdfReader = new PdfReader(originalFilePath))
        using (var pdfWriter = new PdfWriter(signedFilePath))
        {
            var pdfDoc = new PdfDocument(pdfReader, pdfWriter);

            // Create a new page at the end with the same size as the original
            var lastPageSize = pdfDoc.GetLastPage().GetPageSize();
            var newPage = pdfDoc.AddNewPage(new PageSize(lastPageSize));

            // Create a canvas specifically for the new page
            var canvas = new Canvas(new PdfCanvas(newPage), lastPageSize);
            canvas.SetFont(font);

            var table = CreateTable(publicUrl, qrImage);

            canvas.Add(table);

            canvas.Close();
            pdfDoc.Close();
        }

        return Ok(new { signedPdfUrl = publicUrl });
    }
}