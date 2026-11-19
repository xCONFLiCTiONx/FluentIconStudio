using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace FluentIconStudio.Services;

public static class SvgExportService
{
    public static Task<byte[]?> RenderPngAsync(string svgPath, int size, SKColor? iconColor = null, SKColor? canvasBackground = null, double zoomScale = 1.0) =>
        RenderImageAsync(svgPath, size, SKEncodedImageFormat.Png, 100, canvasBackground ?? SKColors.White, true, iconColor, zoomScale);

    public static async Task<byte[]?> RenderImageAsync(
        string svgPath,
        int size,
        SKEncodedImageFormat format,
        int quality = 90,
        SKColor? background = null,
        bool transparent = true,
        SKColor? iconColor = null,
        double zoomScale = 1.0)
    {
        if (size is < 1 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(size), "Image size must be between 1 and 8192 pixels.");
        if (quality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 1 and 100.");

        return await Task.Run<byte[]?>(() =>
        {
            try
            {
                if (!File.Exists(svgPath))
                    return null;

                var svgXml = File.ReadAllText(svgPath);
                if (iconColor.HasValue)
                {
                    var targetHex = $"#{iconColor.Value.Red:X2}{iconColor.Value.Green:X2}{iconColor.Value.Blue:X2}";
                    var bgHex = background.HasValue
                        ? $"#{background.Value.Red:X2}{background.Value.Green:X2}{background.Value.Blue:X2}"
                        : (iconColor.Value == SKColors.White ? "#15171D" : "#FFFFFF");

                    svgXml = RecolorSvgXml(svgXml, targetHex, bgHex);
                }

                return RenderSvgXmlToBytes(svgXml, size, size, format, quality, background, transparent, null, zoomScale);
            }
            catch
            {
                return null;
            }
        });
    }

    public static byte[]? RenderSvgXmlToBytes(
        string svgXml,
        int targetWidth,
        int targetHeight,
        SKEncodedImageFormat format,
        int quality,
        SKColor? background,
        bool transparent,
        SKColor? overrideColor = null,
        double zoomScale = 1.0)
    {
        try
        {
            if (overrideColor.HasValue)
            {
                var hexColor = $"#{overrideColor.Value.Red:X2}{overrideColor.Value.Green:X2}{overrideColor.Value.Blue:X2}";
                svgXml = RecolorSvgXml(svgXml, hexColor, "#15171D");
            }

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svgXml));
            using var sksvg = new SKSvg();
            if (sksvg.Load(stream) is null || sksvg.Picture is null)
                return null;

            var bounds = sksvg.Picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            var canvas = surface.Canvas;
            var clearColor = transparent && format != SKEncodedImageFormat.Jpeg
                ? SKColors.Transparent
                : background ?? SKColors.White;
            canvas.Clear(clearColor);

            float baseScale = Math.Min(targetWidth / bounds.Width, targetHeight / bounds.Height);
            float dx = (targetWidth - (bounds.Width * baseScale)) / 2f;
            float dy = (targetHeight - (bounds.Height * baseScale)) / 2f;

            var matrix = SKMatrix.CreateScale(baseScale, baseScale);
            matrix = SKMatrix.Concat(SKMatrix.CreateTranslation(dx, dy), matrix);

            if (zoomScale > 1.001)
            {
                float z = (float)zoomScale;
                float centerX = targetWidth / 2f;
                float centerY = targetHeight / 2f;

                var zoomMatrix = SKMatrix.CreateTranslation(-centerX, -centerY);
                zoomMatrix = SKMatrix.Concat(SKMatrix.CreateScale(z, z), zoomMatrix);
                zoomMatrix = SKMatrix.Concat(SKMatrix.CreateTranslation(centerX, centerY), zoomMatrix);

                matrix = SKMatrix.Concat(zoomMatrix, matrix);
            }

            canvas.DrawPicture(sksvg.Picture, ref matrix);
            canvas.Flush();

            using var image = surface.Snapshot();
            if (image is null)
                return null;

            using var data = image.Encode(format, Math.Clamp(quality, 1, 100));
            return data?.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static string RecolorSvgXml(string svgXml, string targetColorHex, string backgroundHex)
    {
        if (string.IsNullOrWhiteSpace(svgXml))
            return svgXml;

        try
        {
            var doc = XDocument.Parse(svgXml, LoadOptions.PreserveWhitespace);
            if (doc.Root is null)
                return svgXml;

            foreach (var element in doc.Descendants())
            {
                RecolorAttribute(element, "fill", targetColorHex, backgroundHex);
                RecolorAttribute(element, "stroke", targetColorHex, backgroundHex);

                var styleAttr = element.Attribute("style");
                if (styleAttr is not null && !string.IsNullOrWhiteSpace(styleAttr.Value))
                {
                    var styleVal = styleAttr.Value;
                    styleVal = RecolorStyleProperty(styleVal, "fill", targetColorHex, backgroundHex);
                    styleVal = RecolorStyleProperty(styleVal, "stroke", targetColorHex, backgroundHex);
                    styleAttr.Value = styleVal;
                }
            }

            var rootFill = doc.Root.Attribute("fill")?.Value?.Trim().ToLowerInvariant();
            if (rootFill is null or "currentcolor" or "#000" or "#000000" or "#212121")
            {
                doc.Root.SetAttributeValue("fill", targetColorHex);
            }

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return svgXml;
        }
    }

    private static void RecolorAttribute(XElement element, string attrName, string targetColorHex, string backgroundHex)
    {
        var attr = element.Attribute(attrName);
        if (attr is null)
            return;

        var val = attr.Value.Trim().ToLowerInvariant();
        if (val is "none" or "transparent")
            return;

        if (val is "currentcolor" or "#000" or "#000000" or "#212121" or "#111111" or "#1a1a1a" or "#222" or "#222222" or "black")
        {
            attr.Value = targetColorHex;
        }
        else if (val is "#fff" or "#ffffff" or "white" or "#f1f3f8")
        {
            attr.Value = backgroundHex;
        }
    }

    private static string RecolorStyleProperty(string styleString, string propName, string targetColorHex, string backgroundHex)
    {
        var parts = styleString.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            var kv = parts[i].Split(':');
            if (kv.Length == 2 && kv[0].Trim().Equals(propName, StringComparison.OrdinalIgnoreCase))
            {
                var val = kv[1].Trim().ToLowerInvariant();
                if (val is not ("none" or "transparent"))
                {
                    if (val is "currentcolor" or "#000" or "#000000" or "#212121" or "#111111" or "black")
                        parts[i] = $"{kv[0].Trim()}:{targetColorHex}";
                    else if (val is "#fff" or "#ffffff" or "white")
                        parts[i] = $"{kv[0].Trim()}:{backgroundHex}";
                }
            }
        }
        return string.Join(";", parts);
    }
}
