using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Server.Helper
{
    static class LoadedDocumentsHelper
    {
        public static string NormalizePath(string? path) =>
            string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('/', '\\');

        public static List<IDsDocument> GetDocumentsSnapshot(IDsDocumentService documentService)
        {
            if (documentService == null)
                throw new ArgumentNullException(nameof(documentService));

            return documentService.GetDocuments()
                .Where(d => d != null)
                .ToList();
        }

        public static List<AssemblyDef> GetAssembliesSnapshot(IDsDocumentService documentService) =>
            GetDocumentsSnapshot(documentService)
                .Select(d => d.AssemblyDef)
                .Where(a => a != null)
                .Distinct()
                .ToList()!;

        public static IDsDocument? FindDocument(IDsDocumentService documentService, string? name = null, string? filePath = null) =>
            FindDocuments(documentService, name, filePath).FirstOrDefault();

        public static List<IDsDocument> FindDocuments(IDsDocumentService documentService, string? name = null, string? filePath = null)
        {
            var docs = GetDocumentsSnapshot(documentService);
            var normalizedPath = NormalizePath(filePath);

            if (!string.IsNullOrWhiteSpace(normalizedPath))
                docs = docs
                    .Where(d => !string.IsNullOrWhiteSpace(d.Filename) &&
                        NormalizePath(d.Filename).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (!string.IsNullOrWhiteSpace(name))
                docs = docs
                    .Where(d => d.AssemblyDef != null &&
                        d.AssemblyDef.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            return docs;
        }

        public static AssemblyDef? FindAssembly(IDsDocumentService documentService, string name, string? filePath = null)
        {
            return FindDocument(documentService, name, filePath)?.AssemblyDef;
        }

        public static AssemblyDef? FindAssemblyByModule(IDsDocumentService documentService, string? moduleFilePath, string? moduleName = null)
        {
            if (string.IsNullOrWhiteSpace(moduleFilePath) && string.IsNullOrWhiteSpace(moduleName))
                return null;

            string? normalizedPath = string.IsNullOrWhiteSpace(moduleFilePath)
                ? null
                : moduleFilePath!.Replace('/', '\\');

            return GetAssembliesSnapshot(documentService)
                .FirstOrDefault(assembly => assembly.Modules.Any(module =>
                    (!string.IsNullOrWhiteSpace(normalizedPath) &&
                     !string.IsNullOrWhiteSpace(module.Location) &&
                     module.Location.Replace('/', '\\').Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(moduleName) &&
                     string.Equals(module.Name.String, moduleName, StringComparison.OrdinalIgnoreCase))));
        }

        public static List<TypeDef> GetAllTypesSnapshot(IDsDocumentService documentService)
        {
            return GetAssembliesSnapshot(documentService)
                .SelectMany(a => a.Modules)
                .SelectMany(GetAllTypesRecursive)
                .ToList();
        }

        static IEnumerable<TypeDef> GetAllTypesRecursive(ModuleDef module)
        {
            foreach (var type in module.Types)
            {
                yield return type;
                foreach (var nested in GetNestedTypesRecursive(type))
                    yield return nested;
            }
        }

        static IEnumerable<TypeDef> GetNestedTypesRecursive(TypeDef type)
        {
            foreach (var nested in type.NestedTypes)
            {
                yield return nested;
                foreach (var deep in GetNestedTypesRecursive(nested))
                    yield return deep;
            }
        }
    }
}
