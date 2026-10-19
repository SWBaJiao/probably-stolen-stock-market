using System;

namespace ProbablyStolenStockMarket.Features;

// 无 Unity 依赖的像素中心最近邻采样；像素值原样搬运，保留透明度及调用方的行方向。
internal static class NearestImageSampler
{
    internal sealed record Result(uint[] Pixels, int DrawnWidth, int DrawnHeight, int OffsetX, int OffsetY);

    public static Result Fit(uint[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        ValidateDimensions(sourceWidth, sourceHeight, nameof(sourceWidth));
        ValidateDimensions(targetWidth, targetHeight, nameof(targetWidth));
        if (source.Length != (long)sourceWidth * sourceHeight) throw new ArgumentException("源像素数量与尺寸不符。", nameof(source));
        int drawnWidth, drawnHeight;
        if ((long)targetWidth * sourceHeight <= (long)targetHeight * sourceWidth)
        {
            drawnWidth = targetWidth;
            drawnHeight = Math.Max(1, (int)((long)sourceHeight * targetWidth / sourceWidth));
        }
        else
        {
            drawnHeight = targetHeight;
            drawnWidth = Math.Max(1, (int)((long)sourceWidth * targetHeight / sourceHeight));
        }
        int offsetX = (targetWidth - drawnWidth) / 2, offsetY = (targetHeight - drawnHeight) / 2;
        var result = new uint[targetWidth * targetHeight];
        for (int y = 0; y < drawnHeight; y++)
        {
            int sourceY = Math.Min(sourceHeight - 1, (int)((2L * y + 1) * sourceHeight / (2L * drawnHeight)));
            for (int x = 0; x < drawnWidth; x++)
            {
                int sourceX = Math.Min(sourceWidth - 1, (int)((2L * x + 1) * sourceWidth / (2L * drawnWidth)));
                result[(offsetY + y) * targetWidth + offsetX + x] = source[sourceY * sourceWidth + sourceX];
            }
        }
        return new Result(result, drawnWidth, drawnHeight, offsetX, offsetY);
    }

    private static void ValidateDimensions(int width, int height, string parameter)
    {
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameter, "图像尺寸必须为正数且像素总数不能超出数组范围。");
    }
}
