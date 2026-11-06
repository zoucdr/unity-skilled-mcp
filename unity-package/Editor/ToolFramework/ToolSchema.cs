using System;

namespace UniMcp
{
    /// <summary>
    /// Builds the JSON schema of a single tool from its own parameter subset.
    /// Replaces the old "one schema for every action of a class" behaviour.
    /// </summary>
    public static class ToolSchema
    {
        public static JsonNode Build(ToolSpec spec, bool enableDescriptions = true)
        {
            var properties = new JsonClass();
            var required = new JsonArray();

            foreach (var key in spec.Parameters)
            {
                if (key == null) continue;
                var property = new JsonClass();
                string paramType = !string.IsNullOrEmpty(key.Type) ? key.Type : "string";
                property.Add("type", new JsonData(paramType));

                if (enableDescriptions)
                    property.Add("description", new JsonData(key.Desc));

                if (paramType == "array" && !(key is MethodVector) && !(key is MethodArr))
                {
                    var items = new JsonClass();
                    items.Add("type", new JsonData("string"));
                    property.Add("items", items);
                }

                if (key is MethodArr methodArr)
                {
                    var items = new JsonClass();
                    items.Add("type", new JsonData(methodArr.ItemType));
                    property.Add("items", items);
                }

                if (key is MethodObj methodObj && methodObj.Properties.Count > 0)
                {
                    var objProperties = new JsonClass();
                    foreach (var prop in methodObj.Properties)
                    {
                        var propDef = new JsonClass();
                        propDef.Add("type", new JsonData(prop.Value));
                        if (prop.Value == "array")
                        {
                            var items = new JsonClass();
                            string itemType = "string";
                            if (methodObj.ArrayItemTypes.ContainsKey(prop.Key)) itemType = methodObj.ArrayItemTypes[prop.Key];
                            else if (prop.Key == "position" || prop.Key == "rotation" || prop.Key == "scale" ||
                                     prop.Key == "color" || prop.Key.Contains("vector") || prop.Key.Contains("Vector"))
                                itemType = "number";
                            items.Add("type", new JsonData(itemType));
                            propDef.Add("items", items);
                        }
                        objProperties.Add(prop.Key, propDef);
                    }
                    property.Add("properties", objProperties);
                }

                if (key is MethodVector methodVector)
                {
                    property["type"] = new JsonData("array");
                    var items = new JsonClass();
                    items.Add("type", new JsonData("number"));
                    property.Add("items", items);
                    property.Add("minItems", new JsonData(methodVector.Dimension));
                    property.Add("maxItems", new JsonData(methodVector.Dimension));
                    property.Add("format", new JsonData("vector" + methodVector.Dimension));
                    if (enableDescriptions)
                        property["description"] = new JsonData(key.Desc + " [x, y, z]");
                }

                if (enableDescriptions && key.Examples != null && key.Examples.Count > 0)
                {
                    var examplesArray = new JsonArray();
                    foreach (var example in key.Examples)
                    {
                        if (key is MethodVector && example.StartsWith("[") && example.EndsWith("]"))
                        {
                            try { examplesArray.Add(Json.Parse(example)); }
                            catch { examplesArray.Add(new JsonData(example)); }
                        }
                        else examplesArray.Add(new JsonData(example));
                    }
                    property.Add("examples", examplesArray);
                }

                if (key.EnumValues != null && key.EnumValues.Count > 0)
                {
                    var enumArray = new JsonArray();
                    foreach (var enumValue in key.EnumValues) enumArray.Add(new JsonData(enumValue));
                    property.Add("enum", enumArray);
                }

                if (enableDescriptions && key.DefaultValue != null)
                    property.Add("default", DefaultValue(key.DefaultValue));

                properties.Add(key.Key, property);
            }

            foreach (var name in spec.Required)
                required.Add(new JsonData(name));

            var schema = new JsonClass();
            schema.Add("type", new JsonData("object"));
            schema.Add("properties", properties);
            if (required.Count > 0) schema.Add("required", required);
            return schema;
        }

        private static JsonNode DefaultValue(object value)
        {
            if (value is int || value is float || value is double || value is bool || value is string)
                return Json.FromObject(value);

            var array = new JsonArray();
            var strings = value as string[];
            if (strings != null)
            {
                foreach (var item in strings) array.Add(new JsonData(item));
                return array;
            }
            var floats = value as float[];
            if (floats != null)
            {
                foreach (var item in floats) array.Add(new JsonData(item));
                return array;
            }
            return new JsonData(value.ToString());
        }
    }
}
