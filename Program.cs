using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DocFXYAMLToMarkdown
{
    class Program
    {
        /// <summary>
        /// Formats a string so that it can safely appear in a table,
        /// removing newlines and escaping HTML entities.
        /// </summary>
        /// <param name="input">The string to be formatted.</param>
        /// <returns>The formatted string.</returns>
        public static string FormatForTable(string input)
        {
            if (input == null)
            {
                return "";
            }
            return input.Replace('\n', ' ');
        }

        /// <summary>
        /// Replaces XML-like cross references with a markdown link to the
        /// appropriate <see cref="Item"/>'s page, if possible.
        /// </summary>
        /// <remarks>
        /// This method does two things:
        ///
        /// <![CDATA[
        ///
        /// 1. It replaces the string `{{|` and `|}}` with `{{<` and `>}}`,
        /// which are used by Hugo to indicate shortcodes. 
        ///
        /// ]]>
        ///
        /// 2. It converts any `xref` element in the string, which is a
        /// cross-reference to another <see cref="Item"/>, to an
        /// appropriately formatted link to the <see cref="Item"/>.
        /// </remarks>
        /// <param name="input">The text to check for cross
        /// references.</param>
        /// <returns>The updated text, ready to be used in a markdown
        /// document.</returns>
        public static string FormatCrossReferences(string input)
        {

            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            input = input.Replace("{{|", "{{<");
            input = input.Replace("|}}", ">}}");

            input = System.Web.HttpUtility.HtmlDecode(input);

            var selfClosedTagRegex = new Regex(@"<xref .*?href=""(.*?)"".*?></xref>", RegexOptions.Multiline);

            return selfClosedTagRegex.Replace(input, m =>
            {
                var type = m.Groups[1].Value;

                type = type.Replace("%2c", ",");

                return LinkToType(type);
            });
        }

        /// <summary>
        /// The collection of types that we have seen in the code, and are
        /// generating documentation for.
        /// </summary>
        internal static Dictionary<string, Item> Items { get; set; } = new Dictionary<string, Item>();

        /// <summary>
        /// The collection of types we have seen referred to in the code.
        /// Not all of these types will have documentation available.
        /// </summary>
        /// <remarks>
        /// This collection may include <see cref="Item"/>s that appear in
        /// the <see cref="Items"/> collection.
        /// </remarks>
        internal static Dictionary<string, Item> References { get; set; } = new Dictionary<string, Item>();

        /// <summary>
        /// Maps ItemTypes to an English plural version of the name.
        /// </summary>
        public static readonly Dictionary<Item.ItemType, string> PluralItemTypes = new Dictionary<Item.ItemType, string>() {
            {Item.ItemType.Namespace, "Namespaces"},
            {Item.ItemType.Enum, "Enums"},
            {Item.ItemType.Field, "Fields"},
            {Item.ItemType.Method, "Methods"},
            {Item.ItemType.Class, "Classes"},
            {Item.ItemType.Struct, "Structs"},
            {Item.ItemType.Constructor, "Constructors"},
            {Item.ItemType.Property, "Properties"},
            {Item.ItemType.Delegate, "Delegates"},
            {Item.ItemType.Operator, "Operators"},
            {Item.ItemType.Interface, "Interfaces"},
        };

        /// <summary>
        /// Maps UIDs like "System.String" to C#-style identifiers like
        /// "string"
        /// </summary>
        public static readonly Dictionary<string, string> BuiltInTypeMapping = new Dictionary<string, string> {
            {"System.String", "string"},
            {"System.Boolean", "bool"},
            {"System.Single", "float"},
            {"System.Double", "double"},
        };

        /// <summary>
        /// Items with this type will appear in the menu. This is done to
        /// prevent member methods, fields and properties from appearing in
        /// the menu.
        /// </summary>        
        public static readonly List<Item.ItemType> MenuEntryItemTypes = new List<Item.ItemType>() {
            Item.ItemType.Namespace,
            Item.ItemType.Class,
            Item.ItemType.Struct,
            Item.ItemType.Enum,
            Item.ItemType.Interface,
            Item.ItemType.Delegate,
        };

        /// <summary>
        /// The collection of paths that this process has written to (so
        /// that we can detect writing to the same place twice)
        /// </summary>
        static readonly HashSet<string> WrittenPaths = new HashSet<string>();

        /// <summary>
        /// Generates Markdown documentation from DocFX YAML metadata
        /// files.
        /// </summary>
        /// <param name="inputDirectory">The directory containing
        /// DocFX-generated YAML metadata.</param>
        /// <param name="outputDirectory">The directory in which to create
        /// Markdown documentation.</param>
        /// <param name="overwriteFileDirectory">The directory containing
        /// overwrite files, which can be used to add or replace content
        /// found in the YAML metadata.</param>
        public static void Main(DirectoryInfo inputDirectory, DirectoryInfo outputDirectory, DirectoryInfo overwriteFileDirectory = null)
        {
            foreach (var dir in new[] { inputDirectory, outputDirectory })
            {
                if (dir == null)
                {
                    Console.WriteLine("Error: Both --input-directory and --output-directory must be specified.");

                    Environment.Exit(1);
                    return;
                }

                if (dir.Exists == false)
                {
                    Console.WriteLine($"Error: {dir} is not a directory.");
                    Environment.Exit(1);
                    return;
                }
            }
            IDeserializer deserializer = GetDeserializer();


            // Read the TOC, which will direct us to the rest of the files
            // that we care about
            var tocPath = Path.Combine(inputDirectory.FullName, "toc.yml");
            var document = File.ReadAllText(tocPath);
            var input = new StringReader(document);
            var tableOfContents = deserializer.Deserialize<List<TOCEntry>>(input);


            // Read the YAML file for each entry in the table of contents.
            foreach (var item in tableOfContents)
            {

                // Get the list of UIDs that we want to document.
                IEnumerable<string> documentableUIDs =
                    item.Items.Select(i => i.UID) // UIDs of children
                            .Append(item.UID); // UID of this namespace

                // Read the YAML file for each of these UIDs.
                foreach (var childItemUID in documentableUIDs)
                {
                    var fileName = childItemUID.Replace("`", "-");
                    var childItemPath = Path.Combine(inputDirectory.FullName, fileName + ".yml");

                    var childItemContents = File.ReadAllText(childItemPath);

                    var childItems = deserializer.Deserialize<ItemCollection>(childItemContents);

                    // Gather each of the types that is defined in this
                    // UID...
                    foreach (var childItem in childItems.Items)
                    {
                        Items[childItem.UID] = childItem;
                    }

                    // And each of the (possibly external to this source)
                    // types that is referred to in this UID.
                    foreach (var childItem in childItems.References)
                    {
                        References[childItem.UID] = childItem;
                    }
                }
            }

            foreach (var item in Items.Values)
            {
                // Generate a short UID for each item
                item.ShortUID = GenerateShortUID(item);
            }

            if (overwriteFileDirectory?.Exists ?? false)
            {
                // TODO: read the contents of the files in this directory
                // and apply changes in it to the data that we're reading

                foreach (var child in overwriteFileDirectory.GetFiles("*.md", SearchOption.AllDirectories))
                {
                    Item overwriteItem = ParseOverwriteFile(child);

                    if (Items.ContainsKey(overwriteItem.UID) == false)
                    {
                        Console.WriteLine($"⚠️ WARNING: Overwrite item {child.FullName} overwrites item {overwriteItem.UID}, but no such item exists in the documentation");
                        continue;
                    }

                    var existingItem = Items[overwriteItem.UID];
                    existingItem.OverwriteStringPropertiesWithItem(overwriteItem);

                }
            }

            int documentIndexNumber = 0;

            // Generate the documentation for each item
            foreach (var item in Items.Values.OrderBy(i => i.UID).Where(i => i.DoNotDocument == false))
            {

                string markdown;

                documentIndexNumber += 1;

                if (item.Type == Item.ItemType.Namespace)
                {
                    markdown = GenerateMarkdownForNamespace(item, documentIndexNumber);
                }
                else
                {
                    markdown = GenerateMarkdownForItem(item, documentIndexNumber);
                }

                string itemOutputPath;

                if (item.Type != Item.ItemType.Namespace)
                {
                    itemOutputPath = Path.Combine(outputDirectory.FullName, item.Namespace, PathForItem(item));
                }
                else
                {
                    itemOutputPath = Path.Combine(outputDirectory.FullName, PathForItem(item));
                }

                var itemOutputDirectory = Path.GetDirectoryName(itemOutputPath);

                if (string.IsNullOrEmpty(itemOutputDirectory) == false)
                {
                    Directory.CreateDirectory(itemOutputDirectory);
                }

                File.WriteAllText(itemOutputPath, markdown);

                Console.WriteLine($"📝 Writing {Path.GetRelativePath(outputDirectory.FullName, itemOutputPath)}");

                CheckForWriteCollisions(itemOutputPath);
            }

            // Generate the index file, which links to all namespaces
            var namespaces = Items.Values.Where(t => t.Type == Item.ItemType.Namespace);
            var indexMarkdown = GenerateMarkdownForIndex(namespaces);

            var indexOutputPath = Path.Combine(outputDirectory.FullName, "_index.md");
            Console.WriteLine($"📝 Writing {Path.GetRelativePath(outputDirectory.FullName, indexOutputPath)}");
            File.WriteAllText(indexOutputPath, indexMarkdown);

            CheckForWriteCollisions(indexOutputPath);


        }

        private static IDeserializer GetDeserializer()
        {
            // Create our deserializer, which we'll configure to ignore any
            // YAML fields that we don't have properties for.
            return new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
        }

        /// <summary>
        /// Parses the overwrite file specified by <paramref
        /// name="child"/>, and produces an <see cref="Item"/> that
        /// contains fields that should be merged with another <see
        /// cref="Item"/>.
        /// </summary>
        /// <param name="child">A <see cref="FileInfo"/> that represents an
        /// overwrite file on disk.</param>
        /// <returns>The parsed <see cref="Item"/>.</returns>
        private static Item ParseOverwriteFile(FileInfo child)
        {

            var lines = new Queue<string>(File.ReadAllLines(child.FullName));

            var yamlBuffer = new StringBuilder();

            // Overwrite files are required to start with a YAML header
            if (lines.Dequeue() != "---")
            {
                Console.WriteLine($"ERROR: Expected overwrite file {child.FullName} to start with '---'");
                Environment.Exit(1);
            }

            // The DocFX Overwrite File Spec
            // (https://dotnet.github.io/docfx/tutorial/intro_overwrite_files.html)
            // specifies that the marker that indicates that content should
            // come from the markdown in the document should be "*content",
            // but this causes the YAML parser to fail because no such
            // anchor exists in the document. As a workaround, we'll
            // replace "*content" with this value instead.
            const string contentAnchorPlaceholder = "__content__";

            // Read lines until we hit the end of the header
            while (lines.Peek() != "---")
            {
                try
                {
                    string line = lines.Dequeue();
                    yamlBuffer.AppendLine(line.Replace("*content", contentAnchorPlaceholder));
                }
                catch (InvalidOperationException)
                {
                    Console.WriteLine($"ERROR: Unexpected end of file in overwrite file {child.FullName}");
                    Environment.Exit(1);
                }
            }

            // Remove the end of the header
            lines.Dequeue();

            // Scoop up the remainder into a new string variable
            var markdown = string.Join("\n", lines);

            // Parse the YAML we collected as an Item
            var deserializer = GetDeserializer();
            var item = deserializer.Deserialize<Item>(yamlBuffer.ToString());

            if (string.IsNullOrWhiteSpace(item.UID))
            {
                Console.WriteLine($"ERROR: Overwrite file {child.FullName} does not specify a UID");
                Environment.Exit(1);
            }

            // Overwrite any field of this Item that contains the anchor
            // placeholder with our markdown
            item.ApplyOverwriteMarkdown(markdown, contentAnchorPlaceholder);

            // It's ready to go!
            return item;
        }

        private static void CheckForWriteCollisions(string path)
        {

            if (WrittenPaths.Contains(path.ToLowerInvariant()))
            {
                Console.WriteLine($"ERROR: {path} has already been written to.");
                Environment.Exit(1);
            }

            WrittenPaths.Add(path.ToLowerInvariant());
        }

        private static string GenerateShortUID(Item item)
        {

            // If the Overload for this item is unique, use that
            if (Items.Values.Count(i => i.Overload == item.Overload) == 1)
            {
                return item.Overload.TrimEnd('*');
            }

            // It's not unqiue. Use the full UID instead.
            return item.UID;
        }

        private static string LinkToType(string UID)
        {
            var isArray = UID.EndsWith("[]");

            if (isArray)
            {
                UID = UID.Replace("[]", "");
            }

            var matchedItem = Items.Values.Where(i => i.UID == UID).FirstOrDefault();

            if (matchedItem == null)
            {
                // We have have a reference to this instead
                var matchedReference = References.Values.Where(i => i.UID == UID).FirstOrDefault();

                // If the UID begins with System, link to Microsoft's .NET
                // documentation.
                //
                // TODO: This doesn't work for UIDs that represent generic
                // types, like Dictionary<T, U>.
                if (UID.StartsWith("System."))
                {


                    // trim parameters off, if any
                    var linkUID = Regex.Replace(UID, @"\(.*\)$", "");
                    var url = $"https://docs.microsoft.com/dotnet/api/{linkUID}";

                    // get the last part of the name for display
                    string displayElement;

                    if (BuiltInTypeMapping.ContainsKey(UID))
                    {
                        // If this is a built-in type, don't use the UID,
                        // because it isn't what the user is used to
                        // thinking in terms of - they type "string", not
                        // "System.String", and "float", not
                        // "System.Single". In these cases, perform a
                        // mapping.
                        displayElement = BuiltInTypeMapping[UID];
                    }
                    else
                    {
                        // Otherwise, use the last component of the UID (eg
                        // "System.Action" -> "Action")
                        displayElement = UID.Split(".").Last();
                    }

                    return $"[`{FormatForTable(displayElement)}{(isArray ? "[]" : "")}`]({url})";
                }

                // If the UID begins with "UnityEngine.UI", link to Unity
                // UI's documentation
                //
                // Note that we link to their manual, not to their API
                // docs, because the manual carries a lot more useful
                // information. This may result in some of the links that
                // this code generates not being valid.
                //
                // TODO: Verify links work by querying them and checking
                // for an HTTP 2xx or 3xx status?
                if (UID.StartsWith("UnityEngine.UI."))
                {
                    // trim parameters off, if any
                    UID = Regex.Replace(UID, @"\(.*\)$", "");

                    // trim the first two part of the namespace off
                    // ("UnityEngine.UI")
                    var parts = UID.Split(".").Skip(2);
                    var docsID = string.Join(".", parts);

                    var url = $"https://docs.unity3d.com/Packages/com.unity.ugui@1.0/manual/script-{docsID}.html";

                    // get the last part of the name for display
                    var displayElement = UID.Split(".").Last();

                    return $"[`{FormatForTable(displayElement)}{(isArray ? "[]" : "")}`]({url})";
                }

                // If the UID begins with "UnityEngine", link to Unity's
                // documentation
                if (UID.StartsWith("UnityEngine."))
                {

                    // trim parameters off, if any
                    UID = Regex.Replace(UID, @"\(.*\)$", "");

                    // trim the first part of the namespace off
                    // ("UnityEngine")
                    var parts = UID.Split(".").Skip(1);
                    var docsID = string.Join(".", parts);

                    var url = $"https://docs.unity3d.com/ScriptReference/{docsID}.html";

                    // get the last part of the name for display
                    var displayElement = UID.Split(".").Last();

                    return $"[`{FormatForTable(displayElement)}{(isArray ? "[]" : "")}`]({url})";
                }

                // It's something else, and we don't have a link to it. The
                // best we can do is render a slightly prettier version of
                // the type (i.e. Dictionary<string,int> rather than
                // System.Collections.Generic.Dictionary etc etc)
                if (matchedReference != null)
                {
                    return $"`{matchedReference.Name ?? matchedReference.UID}{(isArray ? "[]" : "")}`";
                }

                // No idea. Give up and just render the UID as-is.
                return $"`{UID}{(isArray ? "[]" : "")}`";
            }
            else
            {

                // This Item came from the source code we're documenting,
                // so we can link directly to our internal docs for this
                // type.
                return $"[`{FormatForTable(matchedItem.Name)}{(isArray ? "[]" : "")}`]({XRef(matchedItem)})";
            }
        }

        private static string XRef(Item item)
        {
            string path = PathForItem(item);

            return $"{{{{<ref \"/{Path.Combine("api", item.Namespace ?? "", path)}\">}}}}";
        }

        /// <summary>
        /// Returns the path to the markdown file used for the Item,
        /// relative to the output directory.
        /// </summary>
        /// <param name="item">The Item to get the path for.</param>
        /// <returns>The path.</returns>
        private static string PathForItem(Item item)
        {
            string path;

            string caseIndex = GetCaseSensitivitySuffixForItem(item);

            switch (item.Type)
            {
                case Item.ItemType.Namespace:
                    path = item.UID + caseIndex + "/_index.md";
                    break;
                case Item.ItemType.Constructor:
                case Item.ItemType.Field:
                case Item.ItemType.Method:
                case Item.ItemType.Operator:
                case Item.ItemType.Property:
                    var parent = Items[item.Parent];
                    if (parent.Type == Item.ItemType.Namespace)
                    {
                        throw new InvalidOperationException($"Parent of item {item.UID} (a {item.Type}) is a namespace - this shouldn't be possible in C#? Bailing out because something more confusing is afoot here.");
                    }
                    var parentUIDWithoutNamespace = GetUIDWithoutNamespace(parent);
                    path = Path.Combine(parentUIDWithoutNamespace, item.ShortUID + caseIndex + ".md");
                    break;
                default:
                    string UIDWithoutNamespace = GetUIDWithoutNamespace(item);
                    path = Path.Combine(UIDWithoutNamespace + caseIndex, "_index.md");
                    break;
            }

            path = path.Replace("#", "_");

            path = path.Replace("`", "-");

            return path;
        }

        /// <summary>
        /// Returns a string that can be appended to the UID of an Item to
        /// disambiguate Items that have the same UID as others when
        /// case-insensitivity is needed (such as on case-insensitive
        /// filesystems.) 
        /// </summary>
        /// <remarks>
        /// If UID is unique and has no other case-insensitive candidates
        /// to disambiguate, this method returns the empty string.
        /// </remarks>
        /// <param name="item">The Item to compute a disambiguation suffix
        /// to.</param>
        /// <returns>A string that can be appended to the end of <paramref
        /// name="item"/>'s UID which uniquely identifies it among other
        /// UIDs that may be case-insensitively the same. </returns>
        private static string GetCaseSensitivitySuffixForItem(Item item)
        {
            // There might exist multiple Items that have the same UID when
            // compared case-insensitively. To handle this, we modify the
            // UID in this way:
            // 1. Build a collection of all (originally-cased) UIDs that
            //    are lowercase-identical to this one.
            // 2. Sort this list lexicographically.
            // 3. Determine the position of this UID in that list, as an
            //    integer.
            // 4. Append this integer to the UID.

            var caseCollisions = Items.Values
                .Where(other => other.UID.ToLowerInvariant() == item.UID.ToLowerInvariant())
                .OrderBy(i => i.UID)
                .ToList();

            if (caseCollisions.Count > 1)
            {
                return caseCollisions.IndexOf(item).ToString();
            }
            else
            {
                return String.Empty;
            }
        }

        /// <summary>
        /// Gets a modified version of <paramref name="item"/>'s UID, with
        /// the namespace removed from the start.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        private static string GetUIDWithoutNamespace(Item item)
        {
            string UIDWithoutNamespace = item.UID;
            string namespacePrefix = item.Namespace + ".";

            if (UIDWithoutNamespace.StartsWith(namespacePrefix))
            {
                UIDWithoutNamespace = UIDWithoutNamespace.Replace(namespacePrefix, "");
            }

            UIDWithoutNamespace = UIDWithoutNamespace.Replace("#", "_");

            return UIDWithoutNamespace;
        }


        /// <summary>
        /// Generates the Markdown for the index file for the
        /// documentation.
        /// </summary>
        /// <param name="namespaces">A collection of <see cref="Item"/>
        /// objects that represent the namespaces that should be
        /// documented.</param>
        /// <returns></returns>
        private static string GenerateMarkdownForIndex(IEnumerable<Item> namespaces)
        {
            // generate links to all namespaces
            StringBuilder stringBuilder = new StringBuilder();

            // front-matter
            stringBuilder.AppendLine($"---");
            stringBuilder.AppendLine($"title: API Documentation");
            stringBuilder.AppendLine($"draft: false");
            stringBuilder.AppendLine($"toc: true");
            stringBuilder.AppendLine($"hide_contents: true");

            // This menu entry is the top-level API entry
            stringBuilder.AppendLine($"menu: ");
            stringBuilder.AppendLine($"     docs:");
            stringBuilder.AppendLine(@"        identifier: ""api""");
            stringBuilder.AppendLine($"---");

            stringBuilder.AppendLine();

            stringBuilder.AppendLine("{{% readfile \"/assets/api_landing.md\" %}}");

            stringBuilder.AppendLine();

            stringBuilder.AppendLine("|Namespace|Description|");
            stringBuilder.AppendLine("|:---|:---|");

            foreach (var @namespace in namespaces)
            {
                stringBuilder.Append("|");
                stringBuilder.Append($"[{@namespace.ID}]({XRef(@namespace)})");
                stringBuilder.Append("|");
                stringBuilder.Append(@namespace.Summary);
                stringBuilder.Append("|");
                stringBuilder.AppendLine();
            }

            // Find all items without summaries, or with syntaxes with
            // parameters or returns that have no description
            var itemsWithoutSummaries = Items.Values
                .Where(i => i.Type != Item.ItemType.Namespace)
                .Where(
                    i =>
                        // The summary is empty 
                        string.IsNullOrEmpty(i.Summary) ||

                        // It takes parameters, and at least one has no
                        // description
                        (i.Syntax?.Parameters.Any(p => string.IsNullOrEmpty(p.Description)) ?? false) ||

                        // It's a method, and its return is not documented
                        (i.Type == Item.ItemType.Method && i.Syntax?.Return != null &&
                            string.IsNullOrEmpty(i.Syntax?.Return?.Description))
                ).Distinct();

            if (itemsWithoutSummaries.Count() > 0)
            {
                // Uncomment to generate a list of items that lack
                // documentation

                stringBuilder.AppendLine("## Items Needing Work");

                foreach (var item in itemsWithoutSummaries)
                {
                    stringBuilder.AppendLine($"* [`{item.NameWithType}`]({XRef(item)})");
                }
            }

            return stringBuilder.ToString();
        }

        public static string GenerateMarkdownForNamespace(Item item, int documentIndexNumber)
        {

            StringBuilder stringBuilder = new StringBuilder();

            // front-matter
            stringBuilder.AppendLine($"---");
            stringBuilder.AppendLine($"title: {item.Name} Namespace");
            stringBuilder.AppendLine($"draft: false");
            stringBuilder.AppendLine($"toc: true");
            stringBuilder.AppendLine($"hide_contents: true");

            // Build the menu item
            stringBuilder.AppendLine($"menu:");
            stringBuilder.AppendLine($"    docs:");

            // This menu entry is a child of the root API entry
            stringBuilder.AppendLine(@"        parent: ""api""");
            stringBuilder.AppendLine($@"        identifier: ""api.{item.UID + GetCaseSensitivitySuffixForItem(item)}""");
            stringBuilder.AppendLine($@"        title: ""{item.Name}""");
            stringBuilder.AppendLine($@"        weight: {documentIndexNumber}");

            stringBuilder.AppendLine($"---");

            var childItemGroups = item.Children
                // Exclude inherited members
                .Where(uid => item.InheritedMembers.Contains(uid) == false)
                // Get full child item by UID
                .Select(childUID => Items[childUID])
                // Group by child item's type
                .GroupBy(i => i.Type);

            stringBuilder.AppendLine(item.Summary);

            foreach (var group in childItemGroups)
            {

                stringBuilder.AppendLine($"## {PluralItemTypes[group.Key]}");

                stringBuilder.AppendLine($"|Name|Description|");
                stringBuilder.AppendLine("|:---|:---|");

                foreach (var childItem in group)
                {
                    stringBuilder.Append("|");

                    stringBuilder.Append($"[{FormatForTable(childItem.Name)}]");
                    stringBuilder.Append($"({XRef(childItem)})");

                    stringBuilder.Append("|");

                    stringBuilder.Append($"{FormatForTable(FormatCrossReferences(childItem.Summary))}");
                    stringBuilder.Append("|");

                    stringBuilder.AppendLine();
                }
            }

            return stringBuilder.ToString();
        }

        public static string GenerateMarkdownForItem(Item item, int documentIndexNumber)
        {
            StringBuilder stringBuilder = new StringBuilder();

            var contentDate = item.Source?.Remote?.Commit?.Author?.Date ?? DateTime.UtcNow;

            // front-matter
            stringBuilder.AppendLine($"---");
            stringBuilder.AppendLine($"title: {item.DisplayName} {item.Type}");
            stringBuilder.AppendLine($"draft: false");
            stringBuilder.AppendLine($"toc: true");
            stringBuilder.AppendLine($"hide_contents: true");
            stringBuilder.AppendLine($"menu:");
            stringBuilder.AppendLine($"    docs:");
            stringBuilder.AppendLine($"        identifier: \"api.{item.UID + GetCaseSensitivitySuffixForItem(item)}\"");
            stringBuilder.AppendLine($"        parent: \"api.{item.Parent + GetCaseSensitivitySuffixForItem(Items[item.Parent])}\"");
            stringBuilder.AppendLine($"        title: \"{item.Name}\"");
            stringBuilder.AppendLine($"        weight: {documentIndexNumber}");
            stringBuilder.AppendLine($"---");

            // Generate the class metadata, which appears at the top of the
            // page in custom formatting
            stringBuilder.AppendLine(@"<div class=""class-metadata"">");
            stringBuilder.AppendLine();

            var metadata = new List<string>();

            // Link to the parent if that's not a namespace
            if (item.Parent != null && Items.ContainsKey(item.Parent) && Items[item.Parent].Type != Item.ItemType.Namespace)
            {
                metadata.Add($"Parent: {LinkToType(item.Parent)}");
            }

            // Show namespace info
            metadata.Add($"Namespace: {LinkToType(item.Namespace)}");
            var assemblyList = string.Join(", ", item.Assemblies.Select(s => s + ".dll"));

            // Show assembly info
            metadata.Add($"Assembly: {assemblyList}");

            // Output this as a single comma-separated line
            stringBuilder.AppendLine(string.Join(", ", metadata));

            stringBuilder.AppendLine("</div>");
            stringBuilder.AppendLine();

            // Now for the body of the documentation:

            // Is this item marked as [Obsolete] in the source?
            var obsoleteMessage = item.Attributes.Where(a => a.Type == "System.ObsoleteAttribute")
                                                 .Select(a => a.Arguments.FirstOrDefault()?.Value)
                                                 .FirstOrDefault();

            // Then add a note explaining it.
            if (obsoleteMessage != null)
            {
                stringBuilder.AppendLine("{{<note>}}");
                stringBuilder.AppendLine($"This {item.Type.ToString().ToLowerInvariant()} is **obsolete** and may be removed from a future version of Yarn Spinner. {obsoleteMessage}");
                stringBuilder.AppendLine("{{</note>}}");

                stringBuilder.AppendLine();
            }

            // Show the summary
            stringBuilder.AppendLine(FormatCrossReferences(item.Summary));
            stringBuilder.AppendLine();

            // Show the source code for the attached Syntax
            if (item.Syntax != null)
            {
                stringBuilder.AppendLine("```csharp");
                stringBuilder.AppendLine(item.Syntax.Content);
                stringBuilder.AppendLine("```");
            }

            // Generate the remarks, if present
            if (item.Remarks != null)
            {
                stringBuilder.AppendLine("## Remarks");
                stringBuilder.AppendLine(FormatCrossReferences(item.Remarks));
            }

            // Generate the examples, if present
            if (item.Example.Count > 0)
            {
                stringBuilder.AppendLine($"## Example{(item.Example.Count == 1 ? "" : "s")}");
                foreach (var example in item.Example)
                {
                    stringBuilder.AppendLine(FormatCrossReferences(example));
                    stringBuilder.AppendLine();
                }
            }

            // Generate the explanation for the syntax (parameters, return
            // type, etc)
            if (item.Syntax != null)
            {
                stringBuilder.AppendLine();
                GenerateMarkdownForSyntax(item, item.Syntax, stringBuilder, 2);
            }

            stringBuilder.AppendLine();

            // Generate documentation for child items (methods, fields,
            // properties, etc)
            if (item.Children != null)
            {
                var childItemGroups = item.Children
                    // // Exclude inherited members .Where(uid =>
                    // item.InheritedMembers.Contains(uid) == false) Get
                    // full child item by UID
                    .Select(childUID => Items[childUID])
                    // Group by child item's type
                    .GroupBy(i => i.Type);

                foreach (var group in childItemGroups)
                {

                    stringBuilder.AppendLine($"## {PluralItemTypes[group.Key]}");

                    stringBuilder.AppendLine("|Name|Description|");
                    stringBuilder.AppendLine("|:---|:---|");

                    foreach (var childItem in group)
                    {
                        stringBuilder.Append($"|");

                        stringBuilder.Append($"[{FormatForTable(childItem.Name)}]");
                        stringBuilder.Append($"({XRef(childItem)})");
                        stringBuilder.Append($"|");

                        var isObsolete = childItem.Attributes.Any(a => a.Type == "System.ObsoleteAttribute");

                        if (isObsolete)
                        {
                            stringBuilder.Append("*Obsolete*");
                            if (string.IsNullOrEmpty(childItem.Summary) == false)
                            {
                                stringBuilder.Append(": ");
                            }
                        }

                        stringBuilder.Append($"{FormatForTable(FormatCrossReferences(childItem.Summary))}");
                        stringBuilder.Append($"|");
                        stringBuilder.AppendLine();
                    }
                }
            }

            // Generate documentation for the exceptions
            if (item.Exceptions.Count > 0)
            {
                stringBuilder.AppendLine("## Exceptions");

                stringBuilder.AppendLine("|Exception|Description|");
                stringBuilder.AppendLine("|:---|:---|");


                foreach (var exception in item.Exceptions)
                {
                    stringBuilder.Append("|");
                    stringBuilder.Append($"{LinkToType(exception.Type)}");
                    stringBuilder.Append("|");

                    if (string.IsNullOrEmpty(exception.Description) == false)
                    {
                        stringBuilder.AppendLine($"{FormatForTable(FormatCrossReferences(exception.Description))}");
                    }
                    stringBuilder.Append("|");


                    stringBuilder.AppendLine();
                };


            }

            // Build the list of see-also entries, from the
            // manually-authored <seealso> tags, the parent type, the list
            // of parameters this item may take, and the return type.
            // Filter out external types from System and UnityEngine, and
            // namespaces.

            var seeAlsoIDs = item.SeeAlso?.Select(s => s.LinkID);

            // If this is a field or property, we want to add 'see-also'
            // references to the parameters and return type.
            if (item.Syntax != null &&
                new[] { Item.ItemType.Property, Item.ItemType.Field }.Contains(item.Type))
            {

                // Start with the manually authored see-also references
                seeAlsoIDs = seeAlsoIDs
                    // Add the types of this item's parameters
                    .Concat(item.Syntax.Parameters?.Select(p => p.Type)

                        // Add the return type of this item
                        .Append(item.Syntax.Return?.Type)

                        // Remove any nulls
                        .Where(i => i != null)

                        // Only include items where we know about the type
                        // (i.e. it has been defined in this source code)
                        .Where(i => Items.ContainsKey(i))

                        // Ensure that any times are not a namespace
                        .Where(i => Items[i].Type != Item.ItemType.Namespace)

                        // Ensure they are not a System or UnityEngine type
                        .Where(i => i.StartsWith("System.") == false)
                        .Where(i => i.StartsWith("UnityEngine.") == false)
                    )

                    // Remove duplicates
                    .Distinct();
            }

            // Don't include this type in the See Also section (otherwise
            // it would be a link back to this page)
            seeAlsoIDs = seeAlsoIDs
                    .Where(i => i != item.UID);

            if (seeAlsoIDs.Count() > 0)
            {
                stringBuilder.AppendLine($"## See Also");

                foreach (var seeAlso in seeAlsoIDs)
                {
                    stringBuilder.Append($"* {LinkToType(seeAlso)}");

                    if (Items.ContainsKey(seeAlso))
                    {
                        stringBuilder.Append($": {FormatCrossReferences(Items[seeAlso].Summary)}");
                    }

                    stringBuilder.AppendLine();
                }
            }

            stringBuilder.AppendLine("## Source");
            stringBuilder.AppendLine($"Defined in [{item.Source?.Path}]({item.Source?.RepoURL}), line {item.Source?.StartLine + 1}.");

            return stringBuilder.ToString();
        }

        public static void GenerateMarkdownForSyntax(Item item, Syntax syntax, StringBuilder stringBuilder, int headerLevel)
        {

            if (syntax.TypeParameters.Count > 0)
            {
                stringBuilder.Append(new string('#', headerLevel));
                stringBuilder.AppendLine(" Type Parameters");

                stringBuilder.AppendLine("|Type Parameter|Description|");
                stringBuilder.AppendLine("|:---|:---|");

                foreach (var typeParameter in syntax.TypeParameters)
                {
                    stringBuilder.Append("|");
                    stringBuilder.Append($"{typeParameter.ID}");
                    stringBuilder.Append("|");

                    if (string.IsNullOrEmpty(typeParameter.Description) == false)
                    {
                        stringBuilder.Append($"{FormatForTable(FormatCrossReferences(typeParameter.Description))}");
                    }

                    stringBuilder.Append("|");

                    stringBuilder.AppendLine();
                }
            }

            if (syntax.Parameters.Count > 0)
            {

                stringBuilder.Append(new string('#', headerLevel));
                stringBuilder.AppendLine(" Parameters");

                stringBuilder.AppendLine("|Parameter|Description|");
                stringBuilder.AppendLine("|:---|:---|");

                foreach (var parameter in syntax.Parameters)
                {
                    stringBuilder.Append("|");
                    stringBuilder.Append($"{LinkToType(parameter.Type)} {parameter.ID}");
                    stringBuilder.Append("|");

                    if (string.IsNullOrEmpty(parameter.Description) == false)
                    {
                        stringBuilder.Append($"{FormatForTable(FormatCrossReferences(parameter.Description))}");
                    }

                    stringBuilder.Append("|");

                    stringBuilder.AppendLine();
                }
            }

            // Show return type information if this is a method, an
            // operation, or a delegate. Things besides methods and
            // delegates (like properties) technically have a "return
            // type", but we don't want to format them like we would a
            // method.
            Item.ItemType[] returnableTypes = new[] { Item.ItemType.Delegate, Item.ItemType.Operator, Item.ItemType.Method };

            if (syntax.Return != null && returnableTypes.Contains(item.Type))
            {
                stringBuilder.Append(new string('#', headerLevel));
                stringBuilder.AppendLine(" Return Type");

                stringBuilder.Append($"{LinkToType(syntax.Return.Type)}");

                if (string.IsNullOrEmpty(syntax.Return.Description) == false)
                {
                    stringBuilder.AppendLine($": {FormatCrossReferences(syntax.Return.Description)}");
                }

                stringBuilder.AppendLine();
            }


            stringBuilder.AppendLine(syntax.Remarks);
        }

        /// <summary>
        /// Represents an entry in the `toc.yml` index file.
        /// </summary>
        public class TOCEntry
        {
            [YamlMember(Alias = "uid", ApplyNamingConventions = false)]
            public string UID { get; set; }
            public string Name { get; set; }
            public List<Item> Items { get; set; }
            public class Item
            {
                [YamlMember(Alias = "uid", ApplyNamingConventions = false)]
                public string UID { get; set; }
                public string Name { get; set; }

            }
        }

        public class ItemCollection
        {
            public List<Item> Items { get; set; }
            public List<Item> References { get; set; }
        }

        /// <summary>
        /// A documentable item. These are loaded from the .yaml files that
        /// DocFX produces. Generally, each Item will have its own Markdown
        /// file.
        /// </summary>
        public class Item
        {
            [YamlMember(Alias = "uid", ApplyNamingConventions = false)]
            public string UID { get; set; }

            [YamlMember(Alias = "id", ApplyNamingConventions = false)]
            public string ID { get; set; }

            public enum ItemType
            {
                Namespace, Enum, Field, Method, Class, Struct, Constructor, Property, Delegate, Operator, Interface
            }

            public ItemType Type { get; set; }

            public string Name { get; set; }

            public string NameWithType { get; set; }

            public string Summary { get; set; }
            public string Namespace { get; set; }
            public string Parent { get; set; }

            public List<string> Example { get; set; } = new List<string>();

            public string Overload { get; set; }

            public List<string> Children { get; set; }

            public List<string> InheritedMembers { get; set; } = new List<string>();
            
            public class Exception
            {
                public string Type { get; set; }
                public string Description { get; set; }
            }

            public List<Exception> Exceptions { get; set; } = new List<Exception>();

            /// <summary>
            /// Describes where this Item can be found in the source code.
            /// </summary>
            public Source Source { get; set; }

            public Syntax Syntax { get; set; }
            public string Remarks { get; set; }
            public List<string> Assemblies { get; set; }

            public bool DoNotDocument { get; set; }
            public string FullName { get; set; }

            [YamlMember(Alias = "seealso", ApplyNamingConventions = false)]
            public List<LinkInfo> SeeAlso { get; set; } = new List<LinkInfo>();



            public string FullNameWithoutNamespace
            {
                get
                {
                    if (Namespace == null)
                    {
                        return FullName;
                    }
                    return Regex.Replace(FullName, $"^{Namespace}.", "");
                }
            }

            public string DisplayName
            {
                get
                {
                    if (FullName == null)
                    {
                        return null;
                    }

                    var items = new List<string> {
                        Parent,
                        Regex.Replace(FullName, @"\(.*\)$", "").Split(".").Last()
                    }.Where(n => n != null);

                    var displayName = string.Join(".", items);

                    if (Namespace == null)
                    {
                        return displayName;
                    }
                    return Regex.Replace(displayName, $"^{Namespace}.", "");
                }
            }

            public List<Attribute> Attributes { get; set; } = new List<Attribute>();
            public string ShortUID { get; internal set; }

            /// <summary>
            /// Finds any string property that currently contains the
            /// string specified in <paramref
            /// name="contentAnchorPlaceholder"/>, and replace it with the
            /// value of <paramref name="markdownContent"/>.
            /// </summary>
            /// <param name="markdownContent">The markdown content to
            /// use.</param>
            /// <param name="contentAnchorPlaceholder">The text to check
            /// for in each string property.</param>
            public void ApplyOverwriteMarkdown(string markdownContent, string contentAnchorPlaceholder)
            {
                System.Reflection.PropertyInfo[] properties = typeof(Item).GetProperties();
                foreach (System.Reflection.PropertyInfo property in properties)
                {
                    if (!(property.GetValue(this) is string currentValue))
                    {
                        continue;
                    }

                    if (currentValue == contentAnchorPlaceholder)
                    {
                        property.SetValue(this, markdownContent);
                    }
                }
            }

            /// <summary>
            /// Overwrites any properties on this <see cref="Item"/> with
            /// the corresponding property of <paramref name="other"/>, if
            /// the value on <paramref name="other"/> is not null, empty or
            /// whitespace.
            /// </summary>
            /// <remarks>
            /// The UID property is never modified by this method.
            /// </remarks>
            /// <param name="other">The other <see cref="Item"/>.</param>
            public void OverwriteStringPropertiesWithItem(Item other)
            {
                System.Reflection.PropertyInfo[] properties = typeof(Item).GetProperties();
                foreach (System.Reflection.PropertyInfo property in properties)
                {
                    // Skip "UID"
                    if (property.Name == "UID")
                    {
                        continue;
                    }

                    // Skip any non-string
                    if (property.PropertyType.IsAssignableFrom(typeof(string)) == false)
                    {
                        continue;
                    }

                    var theirValue = (string)property.GetValue(other);

                    if (string.IsNullOrWhiteSpace(theirValue) == false)
                    {
                        property.SetValue(this, theirValue);
                        Console.WriteLine($"{this.UID} {property.Name} => {theirValue}");
                    }
                }
            }
        }

        /// <summary>
        /// An attribute (e.g. [Obsolete]) applied to an <see
        /// cref="Item"/>.
        /// </summary>
        public class Attribute
        {
            /// <summary>
            /// The type of the <see cref="Attribute"/>.
            /// </summary>
            public String Type { get; set; }

            /// <summary>
            /// The <see cref="Argument"/>s that this <see
            /// cref="Attribute"/> has.
            /// </summary>
            public List<Argument> Arguments { get; set; } = new List<Argument>();

            /// <summary>
            /// An argument used as part of an <see cref="Attribute"/>.
            /// </summary>
            public class Argument
            {
                /// <summary>
                /// The name of the <see cref="Argument"/>.
                /// </summary>
                public string Type { get; set; }

                /// <summary>
                /// The value that the <see cref="Argument"/> is set to.
                /// </summary>
                public string Value { get; set; }
            }
        }

        public class LinkInfo
        {
            [YamlMember(Alias = "linkId", ApplyNamingConventions = false)]
            public string LinkID { get; set; }
        }

        /// <summary>
        /// Represents the source of an <see cref="Item"/>, and defines
        /// where it was found in the original source code (and where that
        /// source code itself may be found.)
        /// </summary>
        public class Source
        {
            public string Base { get; set; }		// Overwrite behaviour: Replace.
            public string Content { get; set; }		// Overwrite behaviour: Replace.
            public int EndLine { get; set; }		// Overwrite behaviour: Replace.
            [YamlMember(Alias = "linkId", ApplyNamingConventions = false)]
            public string ID { get; set; }		// Overwrite behaviour: Replace.
            public bool IsExternal { get; set; }		// Overwrite behaviour: Replace.
            public string Href { get; set; }		// Overwrite behaviour: Replace.
            public string Path { get; set; }		// Overwrite behaviour: Replace.
            public GitSource Remote { get; set; }		// Overwrite behaviour: Merge.
            public int StartLine { get; set; }		// Overwrite behaviour: Replace.

            public string RepoURL
            {
                get
                {
                    if (Remote.RepoURL == null) return null;
                    return Remote.RepoURL + "#L" + (StartLine + 1);
                }
            }

            public class GitSource
            {
                public string Path { get; set; }         // Overwrite behaviour: Replace.
                public string Branch { get; set; }       // Overwrite behaviour: Replace.
                public string Repo { get; set; }         // Overwrite behaviour: Replace.
                public GitCommit Commit { get; set; }        // Overwrite behaviour: Merge.
                public string Key { get; set; }      // Overwrite behaviour: Replace.

                /// <summary>
                /// Converts a git@domain.org:/foo.git style URL to its
                /// https:// equivalent, assuming that the repo follows
                /// GitHub-style conventions between the two.
                /// </summary>
                public string RepoURL
                {
                    get
                    {
                        if (Repo == null)
                        {
                            return null;
                        }

                        string url = Repo;
                        url = Regex.Replace(url, "^.*?@", "https://");
                        url = Regex.Replace(url, "^((https://.*?)):", "$1/");
                        url = Regex.Replace(url, ".git$", "/");
                        url += "/blob/" + Branch + "/" + Path;

                        return url;

                    }
                }

                /// <summary>
                /// Represents a Git commit.
                /// </summary>
                public class GitCommit
                {
                    public User Committer { get; set; }     // Overwrite behaviour: Replace.
                    public User Author { get; set; }        // Overwrite behaviour: Replace.
                    public string Id { get; set; }      // Overwrite behaviour: Replace.
                    public string Message { get; set; }     // Overwrite behaviour: Replace.

                    public class User
                    {
                        public string Name { get; set; }        // Overwrite behaviour: Replace.
                        public string Email { get; set; }       // Overwrite behaviour: Replace.
                        public DateTime Date { get; set; }      // Overwrite behaviour: Replace.
                    }
                }
            }
        }

        /// <summary>
        /// Represents a collection of data intended to overwrite the
        /// content found in the generated YAML file.
        /// </summary>
        public class OverwriteItem
        {
            // Ignore. public string[] Assemblies { get; set; }

            // Ignore. public Attribute[] attributes { get; set; }

            // Ignore. public uid[] children { get; set; }

            // Merge.
            public Source Documentation { get; set; }

            // Replace.
            public string[] Example { get; set; }

            // Merge_keyed_list.
            public Exception[] Exceptions { get; set; }

            // Replace.
            public string FullName { get; set; }

            // Replace.
            public string ID { get; set; }

            // Ignore. public string[] implements { get; set; }

            // Ignore. public string[] inheritance { get; set; }

            // Ignore. public string[] inheritedMembers { get; set; }

            // Replace. public boolean isEii { get; set; }

            // Replace. public Boolean isExtensionMethod { get; set; }

            // Replace.
            public string[] Langs { get; set; }

            // Ignore. public string[] Modifiers { get; set; }


            // Replace.
            public string Name { get; set; }

            // Replace.
            public string Namespace { get; set; }

            // Replace.
            public string Overridden { get; set; }

            // Replace.
            public string Parent { get; set; }

            // Replace.
            public string[] Platform { get; set; }

            // Replace.
            public string Remarks { get; set; }

            // Merge_keyed_list.
            public LinkInfo[] See { get; set; }

            // Merge_keyed_list.
            public LinkInfo[] SeeAlso { get; set; }

            // Merge.
            public Source Source { get; set; }

            // Merge.
            public Syntax Syntax { get; set; }

            // Replace.
            public string Summary { get; set; }

            // Replace.
            public string Type { get; set; }
        }


        public class Syntax
        {
            public class Parameter
            {
                [YamlMember(Alias = "id", ApplyNamingConventions = false)]
                public string ID { get; set; }
                public string Type { get; set; }
                public string Description { get; set; }
            }

            public class ReturnType
            {
                public string Type { get; set; }
                public string Description { get; set; }
            }

            public class TypeParameter {
                [YamlMember(Alias = "id", ApplyNamingConventions = false)]
                public string ID { get; set; }
                public string Description { get; set; }
            }


            public ReturnType Return { get; set; }
            public string Remarks { get; set; }

            public string Content { get; set; }
            public List<Parameter> Parameters { get; set; } = new List<Parameter>();

            public List<TypeParameter> TypeParameters { get; set; } = new List<TypeParameter>();



        }


    }
}
