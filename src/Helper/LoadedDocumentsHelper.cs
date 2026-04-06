using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Server.Helper
{
    static class LoadedDocumentsHelper
    {
        public static List<IDsDocument> GetDocumentsSnapshot(IDsDocumentService documentService)
        {
            if (documentService == null)
                throw new ArgumentNullException(nameof(documentService));

            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                return System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    documentService.GetDocuments()
                        .Where(d => d != null)
                        .ToList());
            }

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

        public static AssemblyDef? FindAssembly(IDsDocumentService documentService, string name, string? filePath = null)
        {
            var docs = GetDocumentsSnapshot(documentService);

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var normalized = filePath!.Replace('/', '\\');
                var byPath = docs.FirstOrDefault(d =>
                    !string.IsNullOrEmpty(d.Filename) &&
                    d.Filename.Replace('/', '\\').Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (byPath?.AssemblyDef != null)
                    return byPath.AssemblyDef;
            }

            return docs
                .Select(d => d.AssemblyDef)
                .FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
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
