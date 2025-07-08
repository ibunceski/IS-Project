using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using QRCoder;
using iText.IO.Font;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Layout;
using iText.Layout.Element;

using Image = System.Drawing.Image;
using Path = System.IO.Path;
using Rectangle = iText.Kernel.Geom.Rectangle;

[Route("api/[controller]")]
[ApiController]
public class PdfSigningController : ControllerBase
{
    private readonly string _baseDir;
    private readonly string _fontPath;
    private readonly string _logoPath;

    public PdfSigningController(IWebHostEnvironment env)
    {
        _baseDir = Path.Combine(env.WebRootPath, "signed");
        _fontPath = Path.Combine(env.WebRootPath, "fonts/NotoSans-Regular.ttf");
        _logoPath = Path.Combine(env.WebRootPath, "images/finki_logo.png");
    }

    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromForm] IFormFile file)
    {
        if (!file.FileName.EndsWith(".pdf"))
            return BadRequest("Only PDF files allowed.");

        Directory.CreateDirectory(_baseDir);
        string uid = Guid.NewGuid().ToString();
        string originalPath = Path.Combine(_baseDir, $"{uid}.pdf");
        string signedPath = Path.Combine(_baseDir, $"signed_{uid}.pdf");
        string publicUrl = $"{Request.Scheme}://{Request.Host}/signed/signed_{uid}.pdf";

        using (var stream = new FileStream(originalPath, FileMode.Create))
            await file.CopyToAsync(stream);

        float pageHeight;
        float lowestY;
        PageSize pageSize;

        using (var reader = new PdfReader(originalPath))
        using (var pdfDoc = new PdfDocument(reader))
        {
            var lastPage = pdfDoc.GetLastPage();
            pageSize = new PageSize(lastPage.GetPageSizeWithRotation());
            pageHeight = pageSize.GetHeight();
            lowestY = MeasureLowestY(lastPage, pdfDoc);
        }

        float overlayHeight = 130;
        float bottomMargin = 120;
        bool enoughSpace = (pageHeight - lowestY - bottomMargin) >= overlayHeight;

        var qrImage = CreateQrWithLogo(publicUrl);

        using var writer = new PdfWriter(signedPath);
        using var resultDoc = new PdfDocument(writer);

        using (var reader = new PdfReader(originalPath))
        using (var originalDoc = new PdfDocument(reader))
        {
            for (int i = 1; i <= originalDoc.GetNumberOfPages(); i++)
                resultDoc.AddPage(originalDoc.GetPage(i).CopyTo(resultDoc));
        }

        PdfPage targetPage = enoughSpace && lowestY > bottomMargin + 50
            ? resultDoc.GetLastPage()
            : resultDoc.AddNewPage(pageSize);

        GenerateOverlayPdf(resultDoc, targetPage, qrImage, publicUrl, pageHeight, enoughSpace);

        return Ok(new { signedPdfUrl = publicUrl });
    }

    private float MeasureLowestY(PdfPage page, PdfDocument doc)
    {
        float lowestY = page.GetPageSizeWithRotation().GetTop();
        bool contentFound = false;
        float pageBottom = page.GetPageSizeWithRotation().GetBottom();
        float pageTop = page.GetPageSizeWithRotation().GetTop();

        var textStrategy = new CustomTextExtractionStrategy();
        new PdfCanvasProcessor(textStrategy).ProcessPageContent(page);
        foreach (var bbox in textStrategy.TextBboxes)
        {
            float y = bbox.GetBottom();
            if (y > pageBottom && y < pageTop - 10 && y < lowestY)
            {
                lowestY = y;
                contentFound = true;
            }
        }

        var imageStrategy = new ImageRenderListener();
        new PdfCanvasProcessor(imageStrategy).ProcessPageContent(page);
        foreach (var bbox in imageStrategy.ImageBboxes)
        {
            float y = bbox.GetBottom();
            if (y > pageBottom && y < pageTop - 10 && y < lowestY)
            {
                lowestY = y;
                contentFound = true;
            }
        }

        foreach (var annotation in page.GetAnnotations())
        {
            var rect = annotation.GetRectangle()?.ToRectangle();
            if (rect != null)
            {
                float y = rect.GetBottom();
                if (y > pageBottom && y < pageTop - 10 && y < lowestY)
                {
                    lowestY = y;
                    contentFound = true;
                }
            }
        }

        var form = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm);
        var fields = form?.GetAsArray(PdfName.Fields);
        if (fields != null)
        {
            for (int i = 0; i < fields.Size(); i++)
            {
                var field = fields.GetAsDictionary(i);
                var rect = field?.GetAsArray(PdfName.Rect);
                if (rect != null && rect.Size() == 4)
                {
                    float y = rect.GetAsNumber(1).FloatValue();
                    if (y > pageBottom && y < pageTop - 10 && y < lowestY)
                    {
                        lowestY = y;
                        contentFound = true;
                    }
                }
            }
        }

        return contentFound && lowestY > pageBottom ? lowestY : pageBottom + 50;
    }

    private Bitmap CreateQrWithLogo(string data)
    {
        var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(data, QRCodeGenerator.ECCLevel.H);
        var qrBytes = new PngByteQRCode(qrData).GetGraphic(20);

        using var tempQr = new Bitmap(new MemoryStream(qrBytes));
        var qr = new Bitmap(tempQr.Width, tempQr.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(qr)) g.DrawImage(tempQr, 0, 0);

        if (!System.IO.File.Exists(_logoPath))
            throw new Exception($"Logo not found at {_logoPath}");

        using var logo = new Bitmap(Image.FromFile(_logoPath));
        int size = (int)(qr.Width * 0.2);
        using var resizedLogo = new Bitmap(logo, new Size(size, size));
        using var qrG = Graphics.FromImage(qr);
        qrG.DrawImage(resizedLogo, (qr.Width - size) / 2, (qr.Height - size) / 2);

        return qr;
    }

    private void GenerateOverlayPdf(PdfDocument doc, PdfPage page, Bitmap qrImage, string publicUrl, float height, bool placeAtBottom)
    {
        var canvas = new Canvas(page, page.GetPageSize());
        var font = PdfFontFactory.CreateFont(_fontPath, PdfEncodings.IDENTITY_H);

        float width = page.GetPageSize().GetWidth();
        float tableWidth = width * 0.85f;
        float leftMargin = (width - tableWidth) / 2;
        float[] colWidths = { tableWidth * 0.15f, tableWidth * 0.7f, tableWidth * 0.15f };

        var table = new Table(colWidths);
        table.AddCell(new Cell().Add(new Paragraph("Потпишува:\nДатум и време:\nВерификација:").SetFont(font).SetFontSize(8)));

        string details = $"Факултет за информатички науки и компјутерско инженерство\n{DateTime.Now:dd.MM.yyyy HH:mm}\nИнформации за верификација на автентичноста на овој документ се достапни со користење на кодот за верификација (QR-кодот) односно на линкот подолу.";
        table.AddCell(new Cell().Add(new Paragraph(details).SetFont(font).SetFontSize(8)));

        using var qrStream = new MemoryStream();
        qrImage.Save(qrStream, ImageFormat.Png);
        var qrItext = new iText.Layout.Element.Image(ImageDataFactory.Create(qrStream.ToArray())).SetWidth(80).SetHeight(80);
        table.AddCell(new Cell().Add(qrItext).SetHorizontalAlignment(iText.Layout.Properties.HorizontalAlignment.CENTER));

        table.AddCell(new Cell(1, 3).Add(new Paragraph(publicUrl).SetFont(font).SetFontSize(8).SetFontColor(iText.Kernel.Colors.ColorConstants.BLUE)));
        table.AddCell(new Cell(1, 3).Add(new Paragraph("Овој документ е официјално потпишан со електронски печат и електронски временски жиг. Автентичноста на печатените копии од овој документ можат да бидат електронски верификувани.").SetFont(font).SetFontSize(8)));

        float y = placeAtBottom ? 20 : height - 130;
        canvas.Add(table.SetMarginLeft(leftMargin).SetFixedPosition(leftMargin, y, tableWidth));
        canvas.Close();
    }
}

public class CustomTextExtractionStrategy : ITextExtractionStrategy
{
    public List<Rectangle> TextBboxes { get; } = new();

    public void EventOccurred(IEventData data, EventType type)
    {
        if (type == EventType.RENDER_TEXT)
        {
            var info = (TextRenderInfo)data;
            TextBboxes.Add(info.GetBaseline().GetBoundingRectangle());
        }
    }

    public string GetResultantText() => string.Empty;
    public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
}

public class ImageRenderListener : IEventListener
{
    public List<Rectangle> ImageBboxes { get; } = new();

    public void EventOccurred(IEventData data, EventType type)
    {
        if (type == EventType.RENDER_IMAGE)
        {
            var info = (ImageRenderInfo)data;
            var matrix = info.GetImageCtm();
            var image = info.GetImage();
            ImageBboxes.Add(new Rectangle(matrix.Get(6), matrix.Get(7), image.GetWidth(), image.GetHeight()));
        }
    }

    public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_IMAGE };
}
