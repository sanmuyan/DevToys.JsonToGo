using System.Text;
using System.Text.Json;

namespace DevToys.JsonToGo.Converters;

// 负责把 JSON 文本转换成 Go 类型定义。
//
// 整体流程分成两层：
// 1. 先把 JSON 推断成内部 Schema 树。
// 2. 再把 Schema 树渲染成 Go 代码。
//
// 这样做的好处是：
// - JSON 解析逻辑和 Go 输出逻辑可以解耦。
// - 后续如果要调整推断规则，只需要改 Schema 构建阶段。
// - 后续如果要调整输出风格，只需要改渲染阶段。
public sealed class JsonToGoConverter(string jsonText)
{
    private readonly string _jsonText = jsonText;

    // 转换入口。
    // 先解析 JSON，再生成 Schema，最后渲染成 Go 类型定义。
    public string Convert()
    {
        using var document = JsonDocument.Parse(_jsonText);
        SchemaNode schema = InferSchema(document.RootElement);
        return RenderRoot(schema);
    }

    // 把单个 JSON 节点映射成内部 Schema 节点。
    //
    // 特别注意：
    // - null / undefined 不直接视为 any
    // - 而是视为 Unknown
    //
    // 这样在数组对象合并时：
    // { "name": "a" } 和 { "name": null }
    // 不会直接把 name 合并成 any，而是优先保留已经确定的 string。
    private static SchemaNode InferSchema(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => InferObjectSchema(element),
            JsonValueKind.Array => InferArraySchema(element),
            JsonValueKind.String => SchemaNode.String(),
            JsonValueKind.True or JsonValueKind.False => SchemaNode.Bool(),
            JsonValueKind.Number => element.TryGetInt64(out _) ? SchemaNode.Int() : SchemaNode.Float(),
            JsonValueKind.Null or JsonValueKind.Undefined => SchemaNode.Unknown(),
            _ => SchemaNode.Any()
        };
    }

    // 为 JSON 对象构建 Schema。
    //
    // 每个属性都独立推断，然后组合成 Object 类型的 Schema。
    private static SchemaNode InferObjectSchema(JsonElement element)
    {
        var properties = new Dictionary<string, SchemaNode>();

        foreach (JsonProperty property in element.EnumerateObject())
        {
            properties[property.Name] = InferSchema(property.Value);
        }

        return SchemaNode.Object(properties);
    }

    // 为 JSON 数组构建 Schema。
    //
    // 数组中的每个元素都会先单独推断类型，
    // 然后通过 MergeSchemas 合并成统一的元素类型。
    //
    // 例如：
    // [1, 2, 3]          => []int
    // [1, 2.5]           => []float64
    // [{"a":1}, {"a":2}] => []struct{ A int ... }
    private static SchemaNode InferArraySchema(JsonElement element)
    {
        SchemaNode? itemSchema = null;

        foreach (JsonElement item in element.EnumerateArray())
        {
            itemSchema = itemSchema is null
                ? InferSchema(item)
                : MergeSchemas(itemSchema, InferSchema(item));
        }

        return SchemaNode.Array(itemSchema ?? SchemaNode.Any());
    }

    // 合并两个表示同一逻辑位置的 Schema。
    //
    // 这个方法主要用于：
    // - 数组合并元素类型
    // - 对象合并同名属性类型
    //
    // 当前规则：
    // - Unknown + 明确类型 => 明确类型
    // - Any + 任意类型 => Any
    // - int + float => float
    // - object + object => 递归按属性合并
    // - array + array => 递归合并元素类型
    // - 其他无法兼容的组合 => any
    private static SchemaNode MergeSchemas(SchemaNode left, SchemaNode right)
    {
        if (left.Kind == SchemaKind.Unknown)
        {
            return right;
        }

        if (right.Kind == SchemaKind.Unknown)
        {
            return left;
        }

        if (left.Kind == SchemaKind.Any || right.Kind == SchemaKind.Any)
        {
            return SchemaNode.Any();
        }

        if (left.Kind == right.Kind)
        {
            return left.Kind switch
            {
                SchemaKind.Object => MergeObjectSchemas(left, right),
                SchemaKind.Array => SchemaNode.Array(MergeSchemas(left.ItemSchema!, right.ItemSchema!)),
                _ => left
            };
        }

        if ((left.Kind == SchemaKind.Int && right.Kind == SchemaKind.Float)
            || (left.Kind == SchemaKind.Float && right.Kind == SchemaKind.Int))
        {
            return SchemaNode.Float();
        }

        return SchemaNode.Any();
    }

    // 按属性维度合并两个对象 Schema。
    //
    // 规则如下：
    // - 左右都有同名属性时，递归合并属性类型
    // - 只有一边存在该属性时，保留已知类型
    //
    // 这里不尝试根据“字段偶尔缺失”去推断 Go 可选字段，
    // 而是优先保留目前能确定的最具体类型。
    private static SchemaNode MergeObjectSchemas(SchemaNode left, SchemaNode right)
    {
        var merged = new Dictionary<string, SchemaNode>(StringComparer.Ordinal);
        var propertyNames = new HashSet<string>(left.Properties!.Keys, StringComparer.Ordinal);
        propertyNames.UnionWith(right.Properties!.Keys);

        foreach (string propertyName in propertyNames)
        {
            bool hasLeft = left.Properties.TryGetValue(propertyName, out SchemaNode? leftSchema);
            bool hasRight = right.Properties.TryGetValue(propertyName, out SchemaNode? rightSchema);

            if (hasLeft && hasRight)
            {
                merged[propertyName] = MergeSchemas(leftSchema!, rightSchema!);
            }
            else if (hasLeft)
            {
                merged[propertyName] = leftSchema!;
            }
            else if (hasRight)
            {
                merged[propertyName] = rightSchema!;
            }
            else
            {
                merged[propertyName] = SchemaNode.Any();
            }
        }

        return SchemaNode.Object(merged);
    }

    // 渲染顶层 Go 声明。
    //
    // 对于顶层是“对象数组”的情况，输出两段定义：
    // type AutoGenerated []AutoGeneratedItem
    // type AutoGeneratedItem struct { ... }
    //
    // 这样比直接输出 []struct{...} 更清晰，也更符合 Go 的常见写法。
    private string RenderRoot(SchemaNode schema)
    {
        if (schema.Kind == SchemaKind.Array && schema.ItemSchema?.Kind == SchemaKind.Object)
        {
            string itemTypeName = "AutoGeneratedItem";
            string itemType = RenderStructType(schema.ItemSchema.Properties!, 0);
            return $"type AutoGenerated []{itemTypeName}{Environment.NewLine}{Environment.NewLine}type {itemTypeName} {itemType}";
        }

        string goType = RenderGoType(schema, 0);
        return $"type AutoGenerated {goType}";
    }

    // 把 Schema 节点渲染成 Go 类型表达式。
    //
    // 这里返回的是局部类型片段，例如：
    // - string
    // - int
    // - []float64
    // - struct { ... }
    // - any
    private string RenderGoType(SchemaNode schema, int indentLevel)
    {
        return schema.Kind switch
        {
            SchemaKind.String => "string",
            SchemaKind.Bool => "bool",
            SchemaKind.Int => "int",
            SchemaKind.Float => "float64",
            SchemaKind.Array => "[]" + RenderGoType(schema.ItemSchema!, indentLevel),
            SchemaKind.Object => RenderStructType(schema.Properties!, indentLevel),
            SchemaKind.Any => "any",
            _ => "any"
        };
    }

    // 把对象 Schema 渲染成匿名 Go struct。
    //
    // 这里会做两件事：
    // - 把 JSON 属性名转换成 Go 的导出字段名
    // - 把原始 JSON 属性名保留到 json tag 中
    //
    // 例如：
    // {"user_name":"a"}
    // 会输出：
    // UserName string `json:"user_name"`
    private string RenderStructType(IReadOnlyDictionary<string, SchemaNode> properties, int indentLevel)
    {
        var builder = new StringBuilder();
        string currentIndent = Indent(indentLevel);
        string fieldIndent = Indent(indentLevel + 1);
        var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<(string FieldName, string FieldType, string JsonPropertyName)>();

        builder.AppendLine("struct {");

        foreach ((string jsonPropertyName, SchemaNode propertySchema) in properties)
        {
            string fieldName = EnsureUniqueFieldName(ToGoFieldName(jsonPropertyName), usedFieldNames);
            string fieldType = RenderGoType(propertySchema, indentLevel + 1);
            fields.Add((fieldName, fieldType, jsonPropertyName));
        }

        int maxFieldNameLength = fields.Count > 0 ? fields.Max(field => field.FieldName.Length) : 0;

        foreach ((string fieldName, string fieldType, string jsonPropertyName) in fields)
        {
            builder.Append(fieldIndent)
                .Append(fieldName.PadRight(maxFieldNameLength))
                .Append(' ')
                .Append(fieldType)
                .Append(' ')
                .Append('`')
                .Append("json:\"")
                .Append(jsonPropertyName.Replace("\"", "\\\""))
                .Append("\"")
                .Append('`')
                .AppendLine();
        }

        builder.Append(currentIndent).Append('}');
        return builder.ToString();
    }

    // 把 JSON 属性名转换成 Go 的导出字段名。
    //
    // 规则：
    // - 非字母数字字符视为分隔符
    // - 每个单词首字母大写
    // - 空名称回退为 Field
    // - 数字开头时补 Field 前缀
    // - 遇到部分 Go 关键字或敏感单词时追加 Field 后缀
    private static string ToGoFieldName(string jsonPropertyName)
    {
        var builder = new StringBuilder();
        bool uppercaseNext = true;

        foreach (char character in jsonPropertyName)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(uppercaseNext ? char.ToUpperInvariant(character) : character);
                uppercaseNext = false;
            }
            else
            {
                uppercaseNext = true;
            }
        }

        string fieldName = builder.ToString();

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            fieldName = "Field";
        }

        if (char.IsDigit(fieldName[0]))
        {
            fieldName = "Field" + fieldName;
        }

        return fieldName switch
        {
            "Type" or "Map" or "Chan" or "Func" or "Var" or "Range" or "Select" or "Go" or "Defer" or "Fallthrough" or "Interface" or "Struct"
                => fieldName + "Field",
            _ => fieldName
        };
    }

    // 保证生成的 Go 字段名唯一。
    //
    // 如果两个 JSON 字段经过规范化后得到同一个 Go 字段名，
    // 后续字段会自动追加数字后缀，例如 UserName2。
    private static string EnsureUniqueFieldName(string fieldName, ISet<string> usedFieldNames)
    {
        if (usedFieldNames.Add(fieldName))
        {
            return fieldName;
        }

        int suffix = 2;
        string candidate = fieldName + suffix;
        while (!usedFieldNames.Add(candidate))
        {
            suffix++;
            candidate = fieldName + suffix;
        }

        return candidate;
    }

    // 生成指定层级的缩进空格。
    private static string Indent(int level) => new(' ', level * 4);

    // 类型推断阶段使用的内部逻辑类型集合。
    //
    // 这里先表达“语义类型”，再映射到 Go 类型字符串，
    // 这样推断规则和输出规则可以独立维护。
    private enum SchemaKind
    {
        // 由 null / undefined 推断出来的占位类型。
        // 表示“当前样本没有足够信息”，后续遇到明确类型时应优先让位。
        Unknown,
        Any,
        String,
        Bool,
        Int,
        Float,
        Object,
        Array
    }

    // JSON 到 Go 之间使用的轻量级中间 Schema 节点。
    //
    // 含义如下：
    // - 标量类型只看 Kind
    // - Object 类型使用 Properties 保存子属性
    // - Array 类型使用 ItemSchema 保存元素类型
    private sealed class SchemaNode
    {
        public SchemaKind Kind { get; }

        public IReadOnlyDictionary<string, SchemaNode>? Properties { get; }

        public SchemaNode? ItemSchema { get; }

        private SchemaNode(SchemaKind kind, IReadOnlyDictionary<string, SchemaNode>? properties = null, SchemaNode? itemSchema = null)
        {
            Kind = kind;
            Properties = properties;
            ItemSchema = itemSchema;
        }

        public static SchemaNode Unknown() => new(SchemaKind.Unknown);

        public static SchemaNode Any() => new(SchemaKind.Any);

        public static SchemaNode String() => new(SchemaKind.String);

        public static SchemaNode Bool() => new(SchemaKind.Bool);

        public static SchemaNode Int() => new(SchemaKind.Int);

        public static SchemaNode Float() => new(SchemaKind.Float);

        public static SchemaNode Object(IReadOnlyDictionary<string, SchemaNode> properties) => new(SchemaKind.Object, properties);

        public static SchemaNode Array(SchemaNode itemSchema) => new(SchemaKind.Array, itemSchema: itemSchema);
    }
}
