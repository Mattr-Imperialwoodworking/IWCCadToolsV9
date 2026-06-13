using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace IWCCadToolsV9.Core
{
    /// <summary>
    /// Renders the right-hand "detail" pane of the Project Navigator as HTML.
    ///
    /// Each tree node's Tag type maps to an ordered list of (FieldKey, Caption, Format)
    /// definitions — the "report writer" config below in <see cref="FieldMapJson"/>.
    /// At render time, the tag object's public properties are read via reflection into
    /// a Dictionary&lt;string, object?&gt; keyed by property name (FieldKey), then the
    /// configured fields are emitted as caption/value rows in order.
    ///
    /// To customize what appears for a given node type:
    ///   1. Find (or add) the entry for that type's name in FieldMapJson.
    ///   2. Add/remove/reorder field entries. "key" must match a public property
    ///      name on the tag class (case-sensitive). "caption" is the natural-language
    ///      label shown to the user. "order" controls row order (ascending).
    ///      "format" is an optional composite-format string, e.g. "{0:MM/dd/yyyy}",
    ///      "{0:N2}", "{0:C}".
    ///
    /// Node types with no entry in FieldMapJson fall back to showing every public
    /// property in declaration order with auto-generated captions (CamelCase -> words).
    /// </summary>
    public static class DetailHtmlRenderer
    {
        private const string CssShell = @"
            body { font-family: 'Segoe UI', sans-serif; font-size: 13px; color: #222; padding: 12px; }
            h2 { margin: 0 0 12px 0; color: #2a5d8f; font-size: 16px; border-bottom: 1px solid #ccc; padding-bottom: 6px; }
            table.detail { border-collapse: collapse; width: 100%; }
            table.detail td { padding: 4px 10px; vertical-align: top; border-bottom: 1px solid #eee; }
            table.detail td.caption { font-weight: 600; color: #555; white-space: nowrap; width: 180px; }
            table.detail td.value { color: #111; }
            table.detail td.value img { max-width: 100%; height: auto; border: 1px solid #ccc; }
            table.detail td.value a { color: #2a5d8f; }
            table.detail td.separator { padding: 2px 0; border-bottom: none; }
            table.detail td.separator hr { border: none; border-top: 1px solid #ccc; margin: 6px 0; }
            table.detail td.section-heading {
                font-weight: 700; color: #2a5d8f; padding-top: 14px; border-bottom: none; font-size: 13px;
            }
            table.detail td.section-heading.collapsible { cursor: pointer; user-select: none; }
            table.detail td.section-heading .section-arrow { display: inline-block; width: 1em; color: #2a5d8f; }
            .empty-value { color: #999; font-style: italic; }
            .info-message { color: #555; font-style: italic; padding: 10px; }
        ";

        private sealed class FieldDef
        {
            public string Key { get; set; } = "";
            public string Caption { get; set; } = "";
            public double Order { get; set; }
            public string? Format { get; set; }

            /// <summary>
            /// "text" (default), "link" (renders an &lt;a href&gt;), or
            /// "image" (renders an &lt;img&gt;; value may be a URL/file path string
            /// or raw byte[] image data, which is embedded as a data: URI).
            /// </summary>
            public string Type { get; set; } = "text";

            /// <summary>
            /// For Type == "link": optional display text shown instead of the raw
            /// URL. Can itself be a {OtherFieldKey} template, e.g. "Open {HdwNo} Cutsheet".
            /// </summary>
            public string? LinkText { get; set; }

            /// <summary>
            /// For Type == "image" with byte[] data: the image MIME type used in the
            /// data: URI, e.g. "image/png", "image/jpeg". Defaults to "image/png".
            /// </summary>
            public string? ImageMimeType { get; set; }

            /// <summary>
            /// For Type == "image": optional max width in pixels (CSS max-width).
            /// </summary>
            public int? MaxWidth { get; set; }

            /// <summary>
            /// For Type == "image": optional max height in pixels (CSS max-height).
            /// </summary>
            public int? MaxHeight { get; set; }

            /// <summary>
            /// For Type == "heading": if true, this section's rows (everything until
            /// the next heading/separator, or end of table) can be toggled open/closed
            /// by clicking the heading.
            /// </summary>
            public bool Collapsible { get; set; }

            /// <summary>
            /// For Type == "heading" with Collapsible == true: if true, the section
            /// starts collapsed.
            /// </summary>
            public bool Collapsed { get; set; }
        }

        /// <summary>
        /// Optional data source for a node type: a table/view to query for additional
        /// columns not present on the tag object. KeyField is the tag's property name
        /// whose value is used to look up KeyColumn in Table via a parameterized query
        /// (SELECT * FROM {Table} WHERE {KeyColumn} = @key).
        /// </summary>
        public sealed class QueryDef
        {
            public string Table { get; set; } = "";
            public string KeyColumn { get; set; } = "";
            public string KeyField { get; set; } = "";
        }

        private sealed class TypeDef
        {
            public string? Title { get; set; }
            public List<FieldDef> Fields { get; set; } = new();
            public QueryDef? Query { get; set; }
        }

        /// <summary>
        /// Path to the editable field map JSON, shipped alongside the assembly at
        /// Resources/DetailFieldMap.json (CopyToOutputDirectory). Edit this file to
        /// control which fields show, their captions, order, and format — no
        /// rebuild required, just reopen/re-select the node (or call ReloadFieldMap()).
        /// </summary>
        private static readonly string FieldMapPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory,
            "Resources", "DetailFieldMap.json");

        /// <summary>
        /// Minimal built-in fallback used only if Resources/DetailFieldMap.json is
        /// missing or fails to parse, so the navigator never breaks. All node types
        /// still render via the reflection-based fallback (every public property).
        /// </summary>
        private const string FallbackFieldMapJson = "{}";

        private static Dictionary<string, TypeDef> _fieldMap = LoadFieldMap();

        /// <summary>
        /// Re-reads Resources/DetailFieldMap.json from disk. Call this if you've
        /// edited the file and want changes to apply without restarting AutoCAD.
        /// </summary>
        public static void ReloadFieldMap()
        {
            _fieldMap = LoadFieldMap();
        }

        private static Dictionary<string, TypeDef> FieldMap => _fieldMap;

        private static Dictionary<string, TypeDef> LoadFieldMap()
        {
            string json = FallbackFieldMapJson;
            try
            {
                if (File.Exists(FieldMapPath))
                    json = File.ReadAllText(FieldMapPath);
            }
            catch
            {
                // Fall back to the minimal built-in map below.
            }

            var result = new Dictionary<string, TypeDef>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var typeProp in doc.RootElement.EnumerateObject())
                {
                    var typeDef = new TypeDef();

                    if (typeProp.Value.TryGetProperty("title", out var titleEl))
                        typeDef.Title = titleEl.GetString();

                    if (typeProp.Value.TryGetProperty("fields", out var fieldsEl))
                    {
                        foreach (var f in fieldsEl.EnumerateArray())
                        {
                            typeDef.Fields.Add(new FieldDef
                            {
                                Key = f.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "",
                                Caption = f.TryGetProperty("caption", out var c) ? c.GetString() ?? "" : "",
                                Order = f.TryGetProperty("order", out var o) ? o.GetDouble() : 0,
                                Format = f.TryGetProperty("format", out var fmt) ? fmt.GetString() : null,
                                Type = f.TryGetProperty("type", out var ty) ? (ty.GetString() ?? "text") : "text",
                                LinkText = f.TryGetProperty("linkText", out var lt) ? lt.GetString() : null,
                                ImageMimeType = f.TryGetProperty("imageMimeType", out var mt) ? mt.GetString() : null,
                                MaxWidth = f.TryGetProperty("maxWidth", out var mw) ? mw.GetInt32() : (int?)null,
                                MaxHeight = f.TryGetProperty("maxHeight", out var mh) ? mh.GetInt32() : (int?)null,
                                Collapsible = f.TryGetProperty("collapsible", out var cl) && cl.GetBoolean(),
                                Collapsed = f.TryGetProperty("collapsed", out var co) && co.GetBoolean()
                            });
                        }
                        typeDef.Fields = typeDef.Fields.OrderBy(x => x.Order).ToList();
                    }

                    if (typeProp.Value.TryGetProperty("query", out var queryEl))
                    {
                        typeDef.Query = new QueryDef
                        {
                            Table = queryEl.TryGetProperty("table", out var tbl) ? tbl.GetString() ?? "" : "",
                            KeyColumn = queryEl.TryGetProperty("keyColumn", out var kc) ? kc.GetString() ?? "" : "",
                            KeyField = queryEl.TryGetProperty("keyField", out var kf) ? kf.GetString() ?? "" : ""
                        };

                        if (!IsValidIdentifier(typeDef.Query.Table) || !IsValidIdentifier(typeDef.Query.KeyColumn)
                            || string.IsNullOrWhiteSpace(typeDef.Query.KeyField))
                        {
                            // Invalid/unsafe table or column name — ignore the query entirely
                            // rather than risk constructing a bad/unsafe SQL statement.
                            typeDef.Query = null;
                        }
                    }

                    result[typeProp.Name] = typeDef;
                }
            }
            catch
            {
                // Malformed field map should never crash the navigator; fall back to reflection-only rendering.
            }
            return result;
        }

        /// <summary>
        /// Validates a SQL table or column identifier referenced from JSON.
        /// Allows optional schema-qualification (e.g. "dbo.Proj_HdwCompile") and
        /// bracketed identifiers (e.g. "[Proj_Hdw_Dash]"). Table/column names
        /// cannot be parameterized in T-SQL, so this guards against malformed
        /// or unsafe values being interpolated into a query string.
        /// </summary>
        private static bool IsValidIdentifier(string? identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return false;

            foreach (var part in identifier.Split('.'))
            {
                string p = part.Trim();
                if (p.StartsWith("[", StringComparison.Ordinal) && p.EndsWith("]", StringComparison.Ordinal))
                    p = p.Substring(1, p.Length - 2);

                if (p.Length == 0) return false;
                if (!System.Text.RegularExpressions.Regex.IsMatch(p, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Looks up the configured query (table/keyColumn/keyField) for a tag's type,
        /// if one is defined in DetailFieldMap.json. Callers (e.g. the navigator)
        /// use this to fetch the key value from the tag via reflection and run a
        /// lookup query, merging the result row's columns in via Render's
        /// extraFields parameter.
        /// </summary>
        public static QueryDef? GetQueryDef(object tag)
        {
            if (tag == null) return null;
            string typeName = tag.GetType().Name;
            return FieldMap.TryGetValue(typeName, out var typeDef) ? typeDef.Query : null;
        }

        /// <summary>
        /// Reads the value of the property named by a QueryDef's KeyField from the
        /// given tag object via reflection. Returns null if the property doesn't exist.
        /// </summary>
        public static object? GetKeyFieldValue(object tag, QueryDef queryDef)
        {
            if (tag == null || queryDef == null || string.IsNullOrEmpty(queryDef.KeyField))
                return null;

            var prop = tag.GetType().GetProperty(queryDef.KeyField, BindingFlags.Public | BindingFlags.Instance);
            return prop?.GetValue(tag);
        }


        public static string RenderMessage(string message)
        {
            var sb = new StringBuilder();
            sb.Append("<html><head><style>").Append(CssShell).Append("</style></head><body>");
            sb.Append("<div class=\"info-message\">").Append(WebUtility.HtmlEncode(message)).Append("</div>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// Renders the detail pane for a tree node's Tag object using the configured
        /// field map (falling back to reflection-over-all-properties if the type
        /// isn't in the map).
        ///
        /// <paramref name="extraFields"/> lets callers inject computed/looked-up
        /// values (e.g. a SQL-derived total) that aren't properties on the tag
        /// object itself. These merge into the same value dictionary and can be
        /// referenced from DetailFieldMap.json by key like any other field.
        /// </summary>
        public static string Render(object tag, IReadOnlyDictionary<string, object?>? extraFields = null)
        {
            if (tag == null)
                return RenderMessage("Select an item to view details.");

            string typeName = tag.GetType().Name;
            var props = tag.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var values = props.ToDictionary(p => p.Name, p => p.GetValue(tag), StringComparer.Ordinal);

            if (extraFields != null)
            {
                foreach (var kv in extraFields)
                    values[kv.Key] = kv.Value;
            }

            List<FieldDef> fields;
            string title;

            if (FieldMap.TryGetValue(typeName, out var typeDef) && typeDef.Fields.Count > 0)
            {
                fields = typeDef.Fields;
                title = ExpandTitle(typeDef.Title, values, FriendlyTypeName(typeName));
            }
            else
            {
                // Fallback: every public property, declaration order, auto-generated captions,
                // plus any extra computed fields appended at the end.
                fields = props.Select((p, i) => new FieldDef
                {
                    Key = p.Name,
                    Caption = CamelCaseToWords(p.Name),
                    Order = i
                }).ToList();

                if (extraFields != null)
                {
                    int start = fields.Count;
                    fields.AddRange(extraFields.Select((kv, i) => new FieldDef
                    {
                        Key = kv.Key,
                        Caption = CamelCaseToWords(kv.Key),
                        Order = start + i
                    }));
                }

                title = FriendlyTypeName(typeName);
            }

            var sb = new StringBuilder();
            sb.Append("<html><head><style>").Append(CssShell).Append("</style></head><body>");
            sb.Append("<h2>").Append(WebUtility.HtmlEncode(title)).Append("</h2>");
            sb.Append("<table class=\"detail\">");

            bool inCollapsibleSection = false;
            int sectionIndex = 0;

            foreach (var f in fields)
            {
                string type = (f.Type ?? "text").ToLowerInvariant();

                if (type == "separator" || type == "divider")
                {
                    if (inCollapsibleSection) { sb.Append("</tbody>"); inCollapsibleSection = false; }
                    sb.Append("<tr><td colspan=\"2\" class=\"separator\"><hr/></td></tr>");
                    continue;
                }

                if (type == "heading")
                {
                    if (inCollapsibleSection) { sb.Append("</tbody>"); inCollapsibleSection = false; }

                    string headingText = ExpandTitle(f.Caption, values, f.Caption);

                    if (f.Collapsible)
                    {
                        sectionIndex++;
                        string sectionId = $"detail-section-{sectionIndex}";
                        string startClass = f.Collapsed ? " collapsed" : "";

                        sb.Append("<tr><td colspan=\"2\" class=\"section-heading collapsible")
                          .Append(startClass)
                          .Append("\" onclick=\"var b=document.getElementById('")
                          .Append(sectionId)
                          .Append("');var a=document.getElementById('")
                          .Append(sectionId)
                          .Append("-arrow');if(b.style.display=='none'){b.style.display='';a.innerHTML='&#9662;';}else{b.style.display='none';a.innerHTML='&#9656;';}\">")
                          .Append("<span id=\"").Append(sectionId).Append("-arrow\" class=\"section-arrow\">")
                          .Append(f.Collapsed ? "&#9656;" : "&#9662;")
                          .Append("</span> ")
                          .Append(WebUtility.HtmlEncode(headingText))
                          .Append("</td></tr>");

                        sb.Append("<tbody id=\"").Append(sectionId).Append("\"")
                          .Append(f.Collapsed ? " style=\"display:none;\"" : "")
                          .Append(">");
                        inCollapsibleSection = true;
                    }
                    else
                    {
                        sb.Append("<tr><td colspan=\"2\" class=\"section-heading\">")
                          .Append(WebUtility.HtmlEncode(headingText))
                          .Append("</td></tr>");
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(f.Key) || !values.TryGetValue(f.Key, out var rawValue))
                    continue;

                string cellHtml = RenderCell(f, rawValue, values, out bool isEmpty);

                sb.Append("<tr><td class=\"caption\">").Append(WebUtility.HtmlEncode(f.Caption)).Append("</td>");
                sb.Append("<td class=\"value");
                if (isEmpty) sb.Append(" empty-value");
                sb.Append("\">");
                sb.Append(cellHtml);
                sb.Append("</td></tr>");
            }

            if (inCollapsibleSection) sb.Append("</tbody>");

            sb.Append("</table></body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// Renders a single value cell according to its FieldDef.Type ("text",
        /// "link", or "image"). Returns the inner HTML for the &lt;td&gt; and sets
        /// <paramref name="isEmpty"/> so the caller can apply the empty-value style.
        /// </summary>
        private static string RenderCell(FieldDef f, object? rawValue, Dictionary<string, object?> values, out bool isEmpty)
        {
            switch ((f.Type ?? "text").ToLowerInvariant())
            {
                case "link":
                {
                    string url = (rawValue as string ?? "").Trim();
                    isEmpty = string.IsNullOrEmpty(url);
                    if (isEmpty) return "(none)";

                    string displayText = string.IsNullOrEmpty(f.LinkText)
                        ? url
                        : ExpandTitle(f.LinkText, values, url);

                    string encodedUrl = WebUtility.HtmlEncode(url);
                    return $"<a href=\"{encodedUrl}\" target=\"_blank\">{WebUtility.HtmlEncode(displayText)}</a>";
                }

                case "image":
                {
                    string? src = BuildImageSrc(rawValue, f);
                    isEmpty = string.IsNullOrEmpty(src);
                    if (isEmpty) return "(none)";

                    var styleParts = new List<string>();
                    if (f.MaxWidth.HasValue) styleParts.Add($"max-width:{f.MaxWidth.Value}px;");
                    if (f.MaxHeight.HasValue) styleParts.Add($"max-height:{f.MaxHeight.Value}px;");
                    string style = styleParts.Count > 0 ? $" style=\"{string.Concat(styleParts)}\"" : "";

                    return $"<img src=\"{WebUtility.HtmlEncode(src!)}\"{style} />";
                }

                default:
                {
                    string displayValue = FormatValue(rawValue, f.Format);
                    isEmpty = string.IsNullOrWhiteSpace(displayValue);
                    return isEmpty ? "(none)" : WebUtility.HtmlEncode(displayValue);
                }
            }
        }

        /// <summary>
        /// Builds an &lt;img src&gt; value for an "image" field:
        ///   - byte[] (e.g. SQL "image"/varbinary column data) -> data: URI (base64).
        ///   - string that looks like an http(s) URL -> used as-is.
        ///   - string that looks like a local/UNC file path -> converted to a file:// URI.
        /// Returns null if the value is empty/unrecognized.
        /// </summary>
        private static string? BuildImageSrc(object? rawValue, FieldDef f)
        {
            switch (rawValue)
            {
                case byte[] bytes when bytes.Length > 0:
                {
                    string mime = string.IsNullOrWhiteSpace(f.ImageMimeType) ? "image/png" : f.ImageMimeType!;
                    return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                }

                case string s when !string.IsNullOrWhiteSpace(s):
                {
                    s = s.Trim();
                    if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        || s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    {
                        return s;
                    }

                    // Treat as a local or UNC file path (e.g. "C:\Images\foo.png" or "\\server\share\foo.png")
                    try { return new Uri(s).AbsoluteUri; }
                    catch { return null; }
                }

                default:
                    return null;
            }
        }

        private static string ExpandTitle(string? template, Dictionary<string, object?> values, string fallback)
        {
            if (string.IsNullOrWhiteSpace(template))
                return fallback;

            string result = template;
            foreach (var kv in values)
            {
                string token = "{" + kv.Key + "}";
                if (result.Contains(token, StringComparison.Ordinal))
                    result = result.Replace(token, FormatValue(kv.Value, null), StringComparison.Ordinal);
            }
            return result;
        }

        private static string FormatValue(object? value, string? format)
        {
            if (value == null) return "";

            if (!string.IsNullOrEmpty(format))
            {
                try { return string.Format(format, value); }
                catch { /* fall through to default formatting */ }
            }

            return value switch
            {
                DateTime dt => dt == default ? "" : dt.ToString("MM/dd/yyyy"),
                bool b => b ? "Yes" : "No",
                _ => value.ToString() ?? ""
            };
        }

        private static string FriendlyTypeName(string typeName)
        {
            // "DashHardwareItemTag" -> "Dash Hardware Item"
            string trimmed = typeName.EndsWith("Tag", StringComparison.Ordinal)
                ? typeName.Substring(0, typeName.Length - 3)
                : typeName;
            return CamelCaseToWords(trimmed);
        }

        private static string CamelCaseToWords(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c) &&
                    (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))))
                {
                    sb.Append(' ');
                }
                sb.Append(c);
            }

            // Tidy up a few common abbreviations / underscores
            return sb.ToString().Replace('_', ' ');
        }
    }
}
