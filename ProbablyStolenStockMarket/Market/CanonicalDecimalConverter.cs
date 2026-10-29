using System;
using System.Globalization;
using Newtonsoft.Json;

namespace ProbablyStolenStockMarket.Market;

// 统一小数输出，避免 Newtonsoft 读取零值时丢失小数位数而改变往返快照。
internal sealed class CanonicalDecimalConverter : JsonConverter
{
    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) => objectType == typeof(decimal) || objectType == typeof(decimal?);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value == null) writer.WriteNull();
        else writer.WriteRawValue(((decimal)value).ToString("0.############################", CultureInfo.InvariantCulture));
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException("小数读取使用序列化器的精确 decimal 解析。");
}
