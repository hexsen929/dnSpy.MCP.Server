/*
    Copyright (C) 2026 @chichicaste

    This file is part of dnSpy MCP Server module.

    dnSpy MCP Server is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy MCP Server is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy MCP Server.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ReflectionBindingFlags = System.Reflection.BindingFlags;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// AgentSmithers-style compatibility tools focused on direct method source and IL editing.
	/// </summary>
	[Export(typeof(AgentCompatibilityTools))]
	public sealed class AgentCompatibilityTools {
		readonly IDocumentTreeView documentTreeView;
		readonly IDecompilerService decompilerService;

		static readonly JsonSerializerOptions PrettyJson = new JsonSerializerOptions { WriteIndented = true };
		static readonly Regex opcodeLineRegex = new Regex(@"^\s*(?:(?<label>[A-Za-z_][\w]*)\s*:\s*)?(?<opcode>\S+)(?:\s+(?<operand>.+))?$", RegexOptions.Compiled);
		static readonly Regex methodSpecRegex = new Regex(@"^(?<type>.+?)::(?<method>[^\(\s]+)(?:\((?<params>.*)\))?$", RegexOptions.Compiled);
		static readonly Regex fieldSpecRegex = new Regex(@"^(?<type>.+?)::(?<field>[^\s]+)$", RegexOptions.Compiled);

		[ImportingConstructor]
		public AgentCompatibilityTools(IDocumentTreeView documentTreeView, IDecompilerService decompilerService) {
			this.documentTreeView = documentTreeView;
			this.decompilerService = decompilerService;
		}

		public CallToolResult GetClassSourcecode(Dictionary<string, object>? arguments) {
			try {
				var (_, type) = ResolveTypeTarget(arguments);
				var decompiler = decompilerService.Decompiler;
				var output = new StringBuilderDecompilerOutput();
				var ctx = new DecompilationContext { CancellationToken = System.Threading.CancellationToken.None };
				decompiler.Decompile(type, output, ctx);
				return Success(output.ToString());
			}
			catch (Exception ex) {
				return Error(ex.Message);
			}
		}

		public CallToolResult GetMethodSourcecode(Dictionary<string, object>? arguments) {
			try {
				var (_, _, method) = ResolveMethodTarget(arguments);
				var decompiler = decompilerService.Decompiler;
				var output = new StringBuilderDecompilerOutput();
				var ctx = new DecompilationContext { CancellationToken = System.Threading.CancellationToken.None };
				decompiler.Decompile(method, output, ctx);
				return Success(output.ToString());
			}
			catch (Exception ex) {
				return Error(ex.Message);
			}
		}

		public CallToolResult GetFunctionOpcodes(Dictionary<string, object>? arguments) {
			try {
				var (assembly, type, method) = ResolveMethodTarget(arguments);
				if (method.Body == null)
					return Error($"Method '{method.FullName}' has no IL body (abstract, extern, or encrypted).");

				var instructions = method.Body.Instructions
					.Select((instr, index) => new {
						LineIndex = index,
						DisplayLine = index + 1,
						Offset = $"IL_{instr.Offset:X4}",
						OpCode = instr.OpCode.Name,
						Operand = GetOperandString(instr)
					})
					.ToList();

				var payload = JsonSerializer.Serialize(new {
					Assembly = assembly.Name.String,
					TypeFullName = type.FullName,
					MethodName = method.Name.String,
					MethodToken = $"0x{method.MDToken.Raw:X8}",
					InstructionCount = instructions.Count,
					Instructions = instructions
				}, PrettyJson);

				return Success(payload);
			}
			catch (Exception ex) {
				return Error(ex.Message);
			}
		}

		public CallToolResult SetFunctionOpcodes(Dictionary<string, object>? arguments) =>
			ApplyFunctionOpcodes(arguments, replaceAll: false);

		public CallToolResult OverwriteFullFunctionOpcodes(Dictionary<string, object>? arguments) =>
			ApplyFunctionOpcodes(arguments, replaceAll: true);

		public CallToolResult UpdateMethodSourcecode(Dictionary<string, object>? arguments) {
			try {
				var (_, type, method) = ResolveMethodTarget(arguments);
				var source = GetRequiredString(arguments, "source", "Source");
				if (string.IsNullOrWhiteSpace(source))
					throw new ArgumentException("source cannot be empty");

				EnsureSourcePatchIsSupported(type, method);
				EnsureEditableMethod(method);

				var wrapperSource = BuildWrapperSource(type, method, source);
				var compileResult = CompileMethodBody(type, method, wrapperSource);
				if (!compileResult.Success || compileResult.CompiledMethod == null) {
					var diagnosticText = string.Join(Environment.NewLine, compileResult.Diagnostics);
					var errorText = "Failed to compile replacement method body." +
						(string.IsNullOrWhiteSpace(diagnosticText) ? string.Empty : Environment.NewLine + diagnosticText);
					return Error(errorText);
				}

				var oldInstructionCount = method.Body?.Instructions.Count ?? 0;
				var importedBody = CloneMethodBodyIntoTarget(compileResult.CompiledType!, compileResult.CompiledMethod!, type, method);
				method.Body = importedBody;
				RefreshTree();

				var result = JsonSerializer.Serialize(new {
					Updated = true,
					TypeFullName = type.FullName,
					MethodName = method.Name.String,
					MethodToken = $"0x{method.MDToken.Raw:X8}",
					OldInstructionCount = oldInstructionCount,
					NewInstructionCount = method.Body.Instructions.Count,
					Note = "The provided source is compiled as the body of a generated replacement method. Save the assembly to persist the patch."
				}, PrettyJson);

				return Success(result);
			}
			catch (Exception ex) {
				return Error(ex.Message);
			}
		}

		CallToolResult ApplyFunctionOpcodes(Dictionary<string, object>? arguments, bool replaceAll) {
			try {
				var (_, type, method) = ResolveMethodTarget(arguments);
				var ilOpcodes = ReadStringArray(arguments, "il_opcodes", "ilOpcodes", "IlOpcodes");
				if (ilOpcodes.Length == 0)
					throw new ArgumentException("il_opcodes must contain at least one instruction");

				var mode = replaceAll
					? "replace_all"
					: (GetOptionalString(arguments, "mode", "Mode") ?? "append").Trim().ToLowerInvariant();
				var lineNumber = replaceAll ? 0 : GetOptionalInt(arguments, "il_line_number", "ilLineNumber") ?? 0;

				EnsureEditableMethod(method);
				var body = method.Body ?? new CilBody();
				method.Body = body;
				var existingCount = body.Instructions.Count;
				if (!replaceAll && (lineNumber < 0 || lineNumber > existingCount))
					throw new ArgumentException($"il_line_number {lineNumber} is out of range. Valid range is 0-{existingCount}.");

				var allowOriginalInstructionTargets = !replaceAll;
				var originalInstructions = body.Instructions.ToList();
				var parsedInstructions = ParseInstructionLines(method, ilOpcodes, originalInstructions, allowOriginalInstructionTargets);
				var removedOriginalTargets = GetRemovedOriginalInstructions(mode, lineNumber, originalInstructions, parsedInstructions.Count);
				EnsureReferencedInstructionsRemain(parsedInstructions, removedOriginalTargets);

				switch (mode) {
				case "append":
				case "insert":
					for (int i = parsedInstructions.Count - 1; i >= 0; i--)
						body.Instructions.Insert(lineNumber, parsedInstructions[i]);
					break;

				case "overwrite":
					for (int i = 0; i < parsedInstructions.Count && lineNumber < body.Instructions.Count; i++)
						body.Instructions.RemoveAt(lineNumber);
					for (int i = parsedInstructions.Count - 1; i >= 0; i--)
						body.Instructions.Insert(lineNumber, parsedInstructions[i]);
					break;

				case "replace_all":
					body.ExceptionHandlers.Clear();
					body.Instructions.Clear();
					foreach (var instruction in parsedInstructions)
						body.Instructions.Add(instruction);
					break;

				default:
					throw new ArgumentException($"Unsupported mode '{mode}'. Use append, insert, overwrite, or replace_all.");
				}

				RefreshTree();

				var result = JsonSerializer.Serialize(new {
					Updated = true,
					TypeFullName = type.FullName,
					MethodName = method.Name.String,
					MethodToken = $"0x{method.MDToken.Raw:X8}",
					Mode = mode,
					InsertedInstructionCount = parsedInstructions.Count,
					TargetLineIndex = lineNumber,
					NewInstructionCount = method.Body.Instructions.Count,
					Note = "Save the assembly to persist IL changes to disk."
				}, PrettyJson);

				return Success(result);
			}
			catch (Exception ex) {
				return Error(ex.Message);
			}
		}

		void EnsureSourcePatchIsSupported(TypeDef type, MethodDef method) {
			if (type.IsInterface || type.IsEnum)
				throw new ArgumentException("update_method_sourcecode only supports class and struct types.");
			if (type.DeclaringType != null)
				throw new ArgumentException("update_method_sourcecode does not yet support nested types.");
			if (type.HasGenericParameters)
				throw new ArgumentException("update_method_sourcecode does not yet support generic types.");
			if (method.HasGenericParameters)
				throw new ArgumentException("update_method_sourcecode does not yet support generic methods.");
		}

		void EnsureEditableMethod(MethodDef method) {
			if (method.HasImplMap) {
				method.ImplMap = null;
				method.Attributes &= ~dnlib.DotNet.MethodAttributes.PinvokeImpl;
				method.ImplAttributes = dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed;
			}

			if (method.Body == null)
				method.Body = new CilBody();
		}

		(AssemblyDef assembly, TypeDef type) ResolveTypeTarget(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");

			var assemblyName = GetRequiredString(arguments, "assembly_name", "Assembly", "assemblyName");
			var filePath = GetOptionalString(arguments, "file_path", "FilePath", "filePath");
			var typeFullName = GetOptionalString(arguments, "type_full_name", "typeFullName");
			var ns = GetOptionalString(arguments, "namespace", "Namespace");
			var className = GetOptionalString(arguments, "class_name", "ClassName");

			if (string.IsNullOrWhiteSpace(typeFullName)) {
				if (string.IsNullOrWhiteSpace(className))
					throw new ArgumentException("type_full_name or namespace + class_name is required");
				typeFullName = string.IsNullOrWhiteSpace(ns) ? className : $"{ns}.{className}";
			}

			var assembly = FindAssemblyByName(assemblyName, filePath)
				?? throw new ArgumentException($"Assembly not found: {assemblyName}");
			var type = FindTypeInAssemblyAll(assembly, typeFullName)
				?? throw new ArgumentException($"Type not found: {typeFullName}");
			return (assembly, type);
		}

		(AssemblyDef assembly, TypeDef type, MethodDef method) ResolveMethodTarget(Dictionary<string, object>? arguments) {
			var (assembly, type) = ResolveTypeTarget(arguments);
			var methodName = GetRequiredString(arguments, "method_name", "MethodName");
			var methodTokenText = GetOptionalString(arguments, "method_token", "MethodToken");
			var parameterCount = GetOptionalInt(arguments, "parameter_count", "parameterCount");

			MethodDef? method = null;
			if (!string.IsNullOrWhiteSpace(methodTokenText)) {
				var token = ParseToken(methodTokenText);
				if (token != 0)
					method = type.Methods.FirstOrDefault(m => m.MDToken.Raw == token);
				if (method == null)
					throw new ArgumentException($"No method with token '{methodTokenText}' found in '{type.FullName}'.");
			}

			if (method == null) {
				var matches = type.Methods
					.Where(m => string.Equals(m.Name.String, methodName, StringComparison.OrdinalIgnoreCase))
					.ToList();
				if (parameterCount.HasValue)
					matches = matches.Where(m => m.Parameters.Count(p => p.IsNormalMethodParameter) == parameterCount.Value).ToList();
				if (matches.Count == 0)
					throw new ArgumentException($"Method '{methodName}' not found in type '{type.FullName}'.");
				if (matches.Count > 1)
					throw new ArgumentException(
						$"Method '{methodName}' is ambiguous in '{type.FullName}'. " +
						$"Use method_token or parameter_count to disambiguate. Tokens: {string.Join(", ", matches.Select(m => $"0x{m.MDToken.Raw:X8}"))}");
				method = matches[0];
			}

			return (assembly, type, method);
		}

		string BuildWrapperSource(TypeDef type, MethodDef method, string bodySnippet) {
			var namespaceLine = string.IsNullOrWhiteSpace(type.Namespace) ? string.Empty : $"namespace {type.Namespace}\n{{\n";
			var namespaceClose = string.IsNullOrWhiteSpace(type.Namespace) ? string.Empty : "}\n";
			var indent = string.IsNullOrWhiteSpace(type.Namespace) ? string.Empty : "\t";
			var members = new List<string>();

			foreach (var field in type.Fields) {
				var declaration = BuildFieldDeclaration(field, method);
				if (!string.IsNullOrWhiteSpace(declaration))
					members.Add(IndentBlock(declaration!, indent + "\t"));
			}

			foreach (var evt in type.Events) {
				var declaration = BuildEventDeclaration(evt, method);
				if (!string.IsNullOrWhiteSpace(declaration))
					members.Add(IndentBlock(declaration!, indent + "\t"));
			}

			foreach (var property in type.Properties) {
				var declaration = BuildPropertyDeclaration(property, method);
				if (!string.IsNullOrWhiteSpace(declaration))
					members.Add(IndentBlock(declaration!, indent + "\t"));
			}

			foreach (var ctor in type.Methods.Where(m => m.IsConstructor)) {
				var declaration = BuildConstructorDeclaration(type, ctor);
				if (!string.IsNullOrWhiteSpace(declaration))
					members.Add(IndentBlock(declaration!, indent + "\t"));
			}

			foreach (var helperMethod in type.Methods.Where(ShouldEmitMethodStub)) {
				var declaration = BuildMethodStubDeclaration(helperMethod);
				if (!string.IsNullOrWhiteSpace(declaration))
					members.Add(IndentBlock(declaration!, indent + "\t"));
			}

			members.Add(IndentBlock(BuildPatchMethodDeclaration(method, bodySnippet), indent + "\t"));
			var memberText = members.Count == 0
				? string.Empty
				: string.Join(Environment.NewLine + Environment.NewLine, members) + Environment.NewLine;

			return
$@"using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
{namespaceLine}{indent}{GetTypeDeclarationPrefix(type)} {GetTypeKeyword(type)} {EscapeIdentifier(type.Name.String)}{BuildBaseTypeClause(type, method)}
{indent}{{
{memberText}{indent}}}
{namespaceClose}";
		}

		static string GetTypeDeclarationPrefix(TypeDef type) {
			if (type.IsAbstract && type.IsSealed && !type.IsValueType)
				return "public static unsafe";
			return "public unsafe";
		}

		static string GetTypeKeyword(TypeDef type) =>
			type.IsValueType && !type.IsEnum ? "struct" : "class";

		string BuildBaseTypeClause(TypeDef type, MethodDef contextMethod) {
			var entries = new List<string>();
			if (!type.IsValueType && type.BaseType != null && NormalizeTypeName(type.BaseType.FullName) != NormalizeTypeName("System.Object"))
				entries.Add(ToCSharpTypeName(type.BaseType.ToTypeSig(), contextMethod));

			foreach (var iface in type.Interfaces) {
				if (iface.Interface != null)
					entries.Add(ToCSharpTypeName(iface.Interface.ToTypeSig(), contextMethod));
			}

			return entries.Count == 0 ? string.Empty : " : " + string.Join(", ", entries.Distinct(StringComparer.Ordinal));
		}

		static string IndentBlock(string block, string indent) =>
			string.Join(Environment.NewLine,
				block.Replace("\r\n", "\n").Split('\n').Select(line => line.Length == 0 ? string.Empty : indent + line));

		string? BuildFieldDeclaration(FieldDef field, MethodDef contextMethod) {
			if (!CanEmitSimpleMemberName(field.Name.String))
				return null;

			var modifiers = new List<string> { "public" };
			if (field.IsStatic)
				modifiers.Add("static");
			if (field.IsInitOnly || field.IsLiteral)
				modifiers.Add("readonly");

			return $"{string.Join(" ", modifiers)} {ToCSharpTypeName(field.FieldType, contextMethod)} {EscapeIdentifier(field.Name.String)};";
		}

		string? BuildEventDeclaration(EventDef evt, MethodDef contextMethod) {
			var accessor = evt.AddMethod ?? evt.RemoveMethod ?? evt.InvokeMethod;
			if (accessor == null || evt.EventType == null || !CanEmitSimpleMemberName(evt.Name.String))
				return null;

			return
$@"public{(accessor.IsStatic ? " static" : string.Empty)} {ToCSharpTypeName(evt.EventType.ToTypeSig(), contextMethod)} {EscapeIdentifier(evt.Name.String)}
{{
	add {{ }}
	remove {{ }}
}}";
		}

		string? BuildPropertyDeclaration(PropertyDef property, MethodDef contextMethod) {
			if (property.PropertySig == null)
				return null;
			if (property.PropertySig.RetType is ByRefSig)
				return null;

			var accessor = property.GetMethod ?? property.SetMethod;
			if (accessor == null)
				return null;

			var propertyName = BuildPropertyName(property, contextMethod);
			if (string.IsNullOrWhiteSpace(propertyName))
				return null;

			var lines = new List<string> {
				$"public{(accessor.IsStatic ? " static" : string.Empty)} {GetReturnTypeDeclaration(property.PropertySig.RetType, contextMethod)} {propertyName}",
				"{"
			};
			if (property.GetMethod != null)
				lines.Add("\tget { throw new global::System.NotSupportedException(); }");
			if (property.SetMethod != null)
				lines.Add("\tset { }");
			lines.Add("}");
			return string.Join(Environment.NewLine, lines);
		}

		string? BuildPropertyName(PropertyDef property, MethodDef contextMethod) {
			var paramCount = property.PropertySig?.Params.Count ?? 0;
			if (paramCount == 0) {
				if (!CanEmitSimpleMemberName(property.Name.String))
					return null;
				return EscapeIdentifier(property.Name.String);
			}

			if (!string.Equals(property.Name.String, "Item", StringComparison.Ordinal) || property.PropertySig == null)
				return null;

			var parameterList = string.Join(", ", property.PropertySig.Params.Select((param, index) =>
				$"{ToCSharpTypeName(param, contextMethod)} {SanitizeIdentifier($"index{index}")}"));
			return $"this[{parameterList}]";
		}

		string? BuildConstructorDeclaration(TypeDef type, MethodDef ctor) {
			if (!ctor.IsConstructor)
				return null;
			if (!ctor.IsStatic && type.IsValueType && ctor.Parameters.Count(p => p.IsNormalMethodParameter) == 0)
				return null;

			var parameterList = BuildParameterList(ctor.Parameters.Where(p => p.IsNormalMethodParameter), ctor);
			var typeName = EscapeIdentifier(type.Name.String);
			return ctor.IsStatic
				? $"static {typeName}()\n{{\n}}"
				: $"public {typeName}({parameterList})\n{{\n}}";
		}

		static bool ShouldEmitMethodStub(MethodDef method) {
			if (method.IsConstructor || method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn || method.IsFire)
				return false;
			return CanEmitSimpleMemberName(method.Name.String);
		}

		string? BuildMethodStubDeclaration(MethodDef method) {
			if (!CanEmitSimpleMemberName(method.Name.String))
				return null;

			return BuildMethodHeader(method, EscapeIdentifier(method.Name.String), includeAsync: false) + Environment.NewLine +
				"{\n\tthrow new global::System.NotSupportedException();\n}";
		}

		string BuildPatchMethodDeclaration(MethodDef method, string bodySnippet) {
			var normalizedBody = bodySnippet.Replace("\r\n", "\n");
			var bodyLines = string.Join(Environment.NewLine, normalizedBody.Split('\n').Select(line => "\t" + line));
			return BuildMethodHeader(method, "__mcp_patch", includeAsync: IsAsyncLikeMethod(method)) + Environment.NewLine +
				"{\n" + bodyLines + "\n}";
		}

		string BuildMethodHeader(MethodDef method, string methodName, bool includeAsync) {
			var modifiers = new List<string> { "public" };
			if (method.IsStatic)
				modifiers.Add("static");
			if (includeAsync)
				modifiers.Add("async");

			var genericSuffix = method.HasGenericParameters
				? $"<{string.Join(", ", method.GenericParameters.Select(gp => EscapeIdentifier(gp.Name.String)))}>"
				: string.Empty;
			var parameterList = BuildParameterList(method.Parameters.Where(p => p.IsNormalMethodParameter), method);
			return $"{string.Join(" ", modifiers)} {GetReturnTypeDeclaration(method.ReturnType, method)} {methodName}{genericSuffix}({parameterList})";
		}

		string BuildParameterList(IEnumerable<Parameter> parameters, MethodDef contextMethod) =>
			string.Join(", ", parameters.Select((parameter, index) =>
				$"{GetParameterPrefix(parameter)}{ToCSharpTypeName(parameter.Type, contextMethod)} {SanitizeIdentifier(string.IsNullOrWhiteSpace(parameter.Name) ? $"arg{index}" : parameter.Name!)}"));

		static string GetParameterPrefix(Parameter parameter) {
			if (HasParamArrayAttribute(parameter))
				return "params ";
			if (parameter.Type is ByRefSig) {
				if (parameter.ParamDef?.IsOut == true)
					return "out ";
				if (parameter.ParamDef?.IsIn == true)
					return "in ";
				return "ref ";
			}
			return string.Empty;
		}

		static bool HasParamArrayAttribute(Parameter parameter) =>
			parameter.ParamDef?.CustomAttributes.Any(attr => NormalizeTypeName(attr.AttributeType.FullName) == NormalizeTypeName("System.ParamArrayAttribute")) == true;

		static string GetReturnTypeDeclaration(TypeSig returnType, MethodDef contextMethod) {
			if (returnType.RemovePinnedAndModifiers() is ByRefSig byRefSig)
				return "ref " + ToCSharpTypeName(byRefSig.Next, contextMethod);
			return ToCSharpTypeName(returnType, contextMethod);
		}

		static bool IsAsyncLikeMethod(MethodDef method) =>
			method.CustomAttributes.Any(attr => NormalizeTypeName(attr.AttributeType.FullName) == NormalizeTypeName("System.Runtime.CompilerServices.AsyncStateMachineAttribute"));

		static bool CanEmitSimpleMemberName(string name) {
			if (string.IsNullOrWhiteSpace(name))
				return false;
			if (name[0] != '_' && !char.IsLetter(name[0]))
				return false;
			for (int i = 1; i < name.Length; i++) {
				var ch = name[i];
				if (ch != '_' && !char.IsLetterOrDigit(ch))
					return false;
			}
			return true;
		}

		static string EscapeIdentifier(string identifier) =>
			identifier.StartsWith("@", StringComparison.Ordinal) ? identifier : "@" + identifier;

		CompileReplacementResult CompileMethodBody(TypeDef type, MethodDef method, string wrapperSource) {
			var syntaxTree = CSharpSyntaxTree.ParseText(wrapperSource);
			var references = BuildCompilationReferences(method.Module).Distinct(new MetadataReferenceComparer()).ToList();
			var compilation = CSharpCompilation.Create(
				"dnSpyMcpPatchAssembly",
				new[] { syntaxTree },
				references,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true));

			using var ms = new MemoryStream();
			var emitResult = compilation.Emit(ms);
			if (!emitResult.Success) {
				var diagnostics = emitResult.Diagnostics
					.Where(d => d.Severity == DiagnosticSeverity.Error)
					.Select(d => d.ToString())
					.ToList();
				return new CompileReplacementResult(null, null, diagnostics);
			}

			ms.Position = 0;
			var patchModule = ModuleDefMD.Load(ms.ToArray());
			var patchType = patchModule.Types.FirstOrDefault(t =>
				t.Name.String == type.Name.String &&
				string.Equals(t.Namespace, type.Namespace, StringComparison.Ordinal))
				?? throw new InvalidOperationException("Compiled patch type was not found in generated assembly.");
			var patchMethod = patchType.Methods.FirstOrDefault(m => m.Name.String == "__mcp_patch")
				?? throw new InvalidOperationException("Compiled patch method '__mcp_patch' was not found.");

			return new CompileReplacementResult(patchType, patchMethod, Array.Empty<string>());
		}

		List<MetadataReference> BuildCompilationReferences(ModuleDef targetModule) {
			var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			void AddPath(string? path) {
				if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
					paths.Add(path!);
			}

			AddPath(typeof(object).Assembly.Location);
			AddPath(typeof(Enumerable).Assembly.Location);
			AddPath(typeof(Uri).Assembly.Location);
			AddPath(typeof(System.Runtime.GCSettings).Assembly.Location);
			AddPath(typeof(System.Threading.Tasks.Task).Assembly.Location);
			AddPath(typeof(System.Threading.CancellationToken).Assembly.Location);

			foreach (var moduleNode in documentTreeView.GetAllModuleNodes()) {
				AddPath(moduleNode.Document?.Filename);
				try {
					AddPath(moduleNode.GetModule().Location);
				}
				catch {
				}
			}

			try {
				AddPath(targetModule.Location);
			}
			catch {
			}

			return paths
				.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
				.ToList();
		}

		CilBody CloneMethodBodyIntoTarget(TypeDef sourceType, MethodDef sourceMethod, TypeDef targetType, MethodDef targetMethod) {
			if (sourceMethod.Body == null)
				throw new InvalidOperationException("Compiled replacement method does not contain a body.");

			var context = new PatchImportContext(sourceType, targetType, sourceMethod, targetMethod);
			var importer = new Importer(targetMethod.Module, ImporterOptions.TryToUseTypeDefs | ImporterOptions.TryToUseMethodDefs | ImporterOptions.TryToUseFieldDefs);
			var sourceBody = sourceMethod.Body;
			var targetBody = new CilBody(sourceBody.InitLocals, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>()) {
				MaxStack = sourceBody.MaxStack,
				KeepOldMaxStack = sourceBody.KeepOldMaxStack
			};

			var localMap = new Dictionary<Local, Local>();
			foreach (var local in sourceBody.Variables) {
				var importedType = ImportTypeSig(local.Type, importer, context)
					?? throw new InvalidOperationException($"Unable to import local type '{local.Type.FullName}'.");
				var importedLocal = new Local(importedType) { Name = local.Name };
				targetBody.Variables.Add(importedLocal);
				localMap[local] = importedLocal;
			}

			var paramMap = new Dictionary<Parameter, Parameter>();
			foreach (var sourceParameter in sourceMethod.Parameters) {
				var targetParameter = targetMethod.Parameters.FirstOrDefault(p => p.MethodSigIndex == sourceParameter.MethodSigIndex);
				if (targetParameter != null)
					paramMap[sourceParameter] = targetParameter;
			}

			var instructionMap = new Dictionary<Instruction, Instruction>();
			foreach (var instruction in sourceBody.Instructions) {
				var clone = Instruction.Create(OpCodes.Nop);
				targetBody.Instructions.Add(clone);
				instructionMap[instruction] = clone;
			}

			for (int i = 0; i < sourceBody.Instructions.Count; i++) {
				var sourceInstruction = sourceBody.Instructions[i];
				var clone = targetBody.Instructions[i];
				clone.OpCode = sourceInstruction.OpCode;
				clone.Operand = ImportOperand(sourceInstruction.Operand, importer, instructionMap, localMap, paramMap, context);
			}

			foreach (var handler in sourceBody.ExceptionHandlers) {
				targetBody.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType) {
					TryStart = handler.TryStart != null ? instructionMap[handler.TryStart] : null,
					TryEnd = handler.TryEnd != null ? instructionMap[handler.TryEnd] : null,
					HandlerStart = handler.HandlerStart != null ? instructionMap[handler.HandlerStart] : null,
					HandlerEnd = handler.HandlerEnd != null ? instructionMap[handler.HandlerEnd] : null,
					FilterStart = handler.FilterStart != null ? instructionMap[handler.FilterStart] : null,
					CatchType = handler.CatchType != null ? ImportTypeReference(handler.CatchType, importer, context) : null
				});
			}

			return targetBody;
		}

		object? ImportOperand(
			object? operand,
			Importer importer,
			Dictionary<Instruction, Instruction> instructionMap,
			Dictionary<Local, Local> localMap,
			Dictionary<Parameter, Parameter> paramMap,
			PatchImportContext context) {
			if (operand == null)
				return null;
			if (operand is Instruction branchTarget)
				return instructionMap[branchTarget];
			if (operand is Instruction[] switchTargets)
				return switchTargets.Select(t => instructionMap[t]).ToArray();
			if (operand is Local local)
				return localMap[local];
			if (operand is Parameter parameter)
				return paramMap.TryGetValue(parameter, out var mappedParameter) ? mappedParameter : parameter;
			if (operand is IMethod method)
				return ImportMethodOperand(method, importer, context);
			if (operand is IField field)
				return ImportFieldOperand(field, importer, context);
			if (operand is ITypeDefOrRef type)
				return ImportTypeReference(type, importer, context);
			if (operand is TypeSig typeSig)
				return ImportTypeSig(typeSig, importer, context);
			if (operand is string || operand is sbyte || operand is byte || operand is int || operand is long || operand is float || operand is double)
				return operand;
			return operand;
		}

		IMethod ImportMethodOperand(IMethod method, Importer importer, PatchImportContext context) {
			if (method is MethodSpec methodSpec && IsPatchTypeReference(methodSpec.Method.DeclaringType, context))
				throw new InvalidOperationException("update_method_sourcecode does not yet support generic instantiations of generated helper methods inside the patch type.");

			if (IsPatchTypeReference(method.DeclaringType, context)) {
				var resolved = ResolveTargetMethod(method, context);
				return resolved.Module == context.TargetMethod.Module ? resolved : importer.Import(resolved);
			}

			return importer.Import(method);
		}

		MethodDef ResolveTargetMethod(IMethod patchMethod, PatchImportContext context) =>
			context.TargetType.Methods.FirstOrDefault(candidate => MethodSignaturesMatch(patchMethod, candidate, context.SourceType, context.TargetType))
				?? throw new InvalidOperationException($"Unable to map generated patch method '{patchMethod.FullName}' back to '{context.TargetType.FullName}'. Local functions and unsupported compiler-generated helpers are not yet supported.");

		IField ImportFieldOperand(IField field, Importer importer, PatchImportContext context) {
			if (IsPatchTypeReference(field.DeclaringType, context)) {
				var resolved = ResolveTargetField(field, context);
				return resolved.Module == context.TargetMethod.Module ? resolved : importer.Import(resolved);
			}

			return importer.Import(field);
		}

		FieldDef ResolveTargetField(IField patchField, PatchImportContext context) {
			var patchFieldType = patchField.FieldSig?.Type;
			return context.TargetType.Fields.FirstOrDefault(candidate =>
				string.Equals(candidate.Name.String, patchField.Name, StringComparison.Ordinal) &&
				GetTypeIdentity(patchFieldType, context.SourceType, context.TargetType) == GetTypeIdentity(candidate.FieldType, context.TargetType, context.TargetType))
				?? throw new InvalidOperationException($"Unable to map generated patch field '{patchField.FullName}' back to '{context.TargetType.FullName}'.");
		}

		ITypeDefOrRef ImportTypeReference(ITypeDefOrRef type, Importer importer, PatchImportContext context) {
			if (IsPatchTypeReference(type, context))
				return context.TargetType;
			if (IsOtherPatchOwnedTypeReference(type, context))
				throw new InvalidOperationException($"update_method_sourcecode introduced helper type '{type.FullName}' inside the generated patch assembly. Lambdas, local functions, and anonymous helper types are not yet supported.");
			return importer.Import(type);
		}

		TypeSig? ImportTypeSig(TypeSig? typeSig, Importer importer, PatchImportContext context) {
			if (typeSig is null)
				return null;

			var module = context.TargetMethod.Module;
			switch (typeSig.ElementType) {
			case ElementType.Void: return module.CorLibTypes.Void;
			case ElementType.Boolean: return module.CorLibTypes.Boolean;
			case ElementType.Char: return module.CorLibTypes.Char;
			case ElementType.I1: return module.CorLibTypes.SByte;
			case ElementType.U1: return module.CorLibTypes.Byte;
			case ElementType.I2: return module.CorLibTypes.Int16;
			case ElementType.U2: return module.CorLibTypes.UInt16;
			case ElementType.I4: return module.CorLibTypes.Int32;
			case ElementType.U4: return module.CorLibTypes.UInt32;
			case ElementType.I8: return module.CorLibTypes.Int64;
			case ElementType.U8: return module.CorLibTypes.UInt64;
			case ElementType.R4: return module.CorLibTypes.Single;
			case ElementType.R8: return module.CorLibTypes.Double;
			case ElementType.String: return module.CorLibTypes.String;
			case ElementType.TypedByRef: return module.CorLibTypes.TypedReference;
			case ElementType.I: return module.CorLibTypes.IntPtr;
			case ElementType.U: return module.CorLibTypes.UIntPtr;
			case ElementType.Object: return module.CorLibTypes.Object;
			case ElementType.Ptr: return new PtrSig(ImportTypeSig(typeSig.Next, importer, context)!);
			case ElementType.ByRef: return new ByRefSig(ImportTypeSig(typeSig.Next, importer, context)!);
			case ElementType.ValueType: return CreateClassOrValueTypeSig(((ClassOrValueTypeSig)typeSig).TypeDefOrRef, true, importer, context);
			case ElementType.Class: return CreateClassOrValueTypeSig(((ClassOrValueTypeSig)typeSig).TypeDefOrRef, false, importer, context);
			case ElementType.Var:
				var genericVar = (GenericVar)typeSig;
				return new GenericVar(genericVar.Number, genericVar.OwnerType);
			case ElementType.ValueArray:
				var valueArray = (ValueArraySig)typeSig;
				return new ValueArraySig(ImportTypeSig(typeSig.Next, importer, context)!, valueArray.Size);
			case ElementType.FnPtr:
				var fnPtr = (FnPtrSig)typeSig;
				return new FnPtrSig(fnPtr.Signature);
			case ElementType.SZArray:
				return new SZArraySig(ImportTypeSig(typeSig.Next, importer, context)!);
			case ElementType.MVar:
				var genericMVar = (GenericMVar)typeSig;
				return new GenericMVar(genericMVar.Number, genericMVar.OwnerMethod);
			case ElementType.CModReqd:
				var cmodReq = (ModifierSig)typeSig;
				return new CModReqdSig(ImportTypeReference(cmodReq.Modifier, importer, context), ImportTypeSig(typeSig.Next, importer, context)!);
			case ElementType.CModOpt:
				var cmodOpt = (ModifierSig)typeSig;
				return new CModOptSig(ImportTypeReference(cmodOpt.Modifier, importer, context), ImportTypeSig(typeSig.Next, importer, context)!);
			case ElementType.Module:
				var moduleSig = (ModuleSig)typeSig;
				return new ModuleSig(moduleSig.Index, ImportTypeSig(typeSig.Next, importer, context)!);
			case ElementType.Sentinel:
				return new SentinelSig();
			case ElementType.Pinned:
				return new PinnedSig(ImportTypeSig(typeSig.Next, importer, context)!);
			case ElementType.Array:
				var arraySig = (ArraySig)typeSig;
				return new ArraySig(ImportTypeSig(typeSig.Next, importer, context)!, arraySig.Rank, arraySig.Sizes, arraySig.LowerBounds);
			case ElementType.GenericInst:
				var genericInst = (GenericInstSig)typeSig;
				var args = new List<TypeSig>(genericInst.GenericArguments.Count);
				foreach (var arg in genericInst.GenericArguments)
					args.Add(ImportTypeSig(arg, importer, context)!);
				return new GenericInstSig((ClassOrValueTypeSig)ImportTypeSig(genericInst.GenericType, importer, context)!, args);
			default:
				var tdor = typeSig.ToTypeDefOrRef();
				if (tdor != null)
					return CreateClassOrValueTypeSig(tdor, typeSig.ElementType == ElementType.ValueType, importer, context);
				return typeSig;
			}
		}

		TypeSig CreateClassOrValueTypeSig(ITypeDefOrRef type, bool isValueType, Importer importer, PatchImportContext context) {
			var importedType = ImportTypeReference(type, importer, context);
			var corLibType = context.TargetMethod.Module.CorLibTypes.GetCorLibTypeSig(importedType);
			if (corLibType != null)
				return corLibType;
			return isValueType ? new ValueTypeSig(importedType) : new ClassSig(importedType);
		}

		static bool IsPatchTypeReference(ITypeDefOrRef? type, PatchImportContext context) {
			if (type == null)
				return false;

			var resolved = type.ResolveTypeDef();
			if (resolved != null)
				return resolved.Module == context.SourceType.Module && NormalizeTypeName(resolved.FullName) == NormalizeTypeName(context.SourceType.FullName);

			return NormalizeTypeName(type.FullName) == NormalizeTypeName(context.SourceType.FullName);
		}

		static bool IsOtherPatchOwnedTypeReference(ITypeDefOrRef? type, PatchImportContext context) {
			if (type == null)
				return false;

			var resolved = type.ResolveTypeDef();
			return resolved != null && resolved.Module == context.SourceType.Module && NormalizeTypeName(resolved.FullName) != NormalizeTypeName(context.SourceType.FullName);
		}

		static bool MethodSignaturesMatch(IMethod patchMethod, MethodDef targetMethod, TypeDef sourceType, TypeDef targetType) {
			var patchSig = patchMethod.MethodSig;
			var targetSig = targetMethod.MethodSig;
			if (patchSig == null || targetSig == null)
				return false;
			if (!string.Equals(patchMethod.Name, targetMethod.Name.String, StringComparison.Ordinal))
				return false;
			if (patchSig.Params.Count != targetSig.Params.Count || patchSig.GenParamCount != targetSig.GenParamCount || patchSig.HasThis != targetSig.HasThis)
				return false;
			if (GetTypeIdentity(patchSig.RetType, sourceType, targetType) != GetTypeIdentity(targetSig.RetType, targetType, targetType))
				return false;
			for (int i = 0; i < patchSig.Params.Count; i++) {
				if (GetTypeIdentity(patchSig.Params[i], sourceType, targetType) != GetTypeIdentity(targetSig.Params[i], targetType, targetType))
					return false;
			}
			return true;
		}

		static string GetTypeIdentity(TypeSig? typeSig, TypeDef sourceType, TypeDef targetType) {
			if (typeSig == null)
				return string.Empty;

			typeSig = typeSig.RemovePinnedAndModifiers();
			switch (typeSig.ElementType) {
			case ElementType.ByRef:
				return "ref:" + GetTypeIdentity(typeSig.Next, sourceType, targetType);
			case ElementType.Ptr:
				return "ptr:" + GetTypeIdentity(typeSig.Next, sourceType, targetType);
			case ElementType.SZArray:
				return "sz[]:" + GetTypeIdentity(typeSig.Next, sourceType, targetType);
			case ElementType.Array:
				var arraySig = (ArraySig)typeSig;
				return $"array[{arraySig.Rank}]:" + GetTypeIdentity(arraySig.Next, sourceType, targetType);
			case ElementType.GenericInst:
				var genericInst = (GenericInstSig)typeSig;
				return $"gi:{GetTypeIdentity(genericInst.GenericType, sourceType, targetType)}<{string.Join(",", genericInst.GenericArguments.Select(arg => GetTypeIdentity(arg, sourceType, targetType)))}>";
			case ElementType.Var:
				return "!" + ((GenericVar)typeSig).Number.ToString(CultureInfo.InvariantCulture);
			case ElementType.MVar:
				return "!!" + ((GenericMVar)typeSig).Number.ToString(CultureInfo.InvariantCulture);
			case ElementType.Class:
			case ElementType.ValueType:
				var typeRef = ((TypeDefOrRefSig)typeSig).TypeDefOrRef;
				var normalized = NormalizeTypeName(CleanTypeName(typeRef.FullName));
				return normalized == NormalizeTypeName(sourceType.FullName)
					? NormalizeTypeName(targetType.FullName)
					: normalized;
			default:
				return NormalizeTypeName(CleanTypeName(typeSig.FullName));
			}
		}

		List<Instruction> ParseInstructionLines(MethodDef targetMethod, IEnumerable<string> ilOpcodes, IReadOnlyList<Instruction> originalInstructions, bool allowOriginalInstructionTargets) {
			var parsed = ilOpcodes
				.Select((raw, index) => ParseInstructionSpec(raw, index))
				.Where(spec => spec != null)
				.Cast<ParsedInstructionSpec>()
				.ToList();

			var instructions = new List<Instruction>(parsed.Count);
			var labelMap = new Dictionary<string, Instruction>(StringComparer.OrdinalIgnoreCase);
			foreach (var spec in parsed) {
				var placeholder = Instruction.Create(OpCodes.Nop);
				instructions.Add(placeholder);
				if (!string.IsNullOrWhiteSpace(spec.Label)) {
					if (labelMap.ContainsKey(spec.Label!))
						throw new ArgumentException($"Duplicate label '{spec.Label}'.");
					labelMap[spec.Label!] = placeholder;
				}
			}

			for (int i = 0; i < parsed.Count; i++) {
				var spec = parsed[i];
				instructions[i].OpCode = spec.OpCode;
				instructions[i].Operand = CreateOperandForInstruction(targetMethod, spec, labelMap, originalInstructions, allowOriginalInstructionTargets);
			}

			return instructions;
		}

		ParsedInstructionSpec? ParseInstructionSpec(string raw, int index) {
			var line = raw?.Trim() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//", StringComparison.Ordinal))
				return null;

			var match = opcodeLineRegex.Match(line);
			if (!match.Success)
				throw new ArgumentException($"Unable to parse opcode line {index}: '{raw}'");

			var opName = match.Groups["opcode"].Value.Trim();
			var normalized = opName.Replace(".", "_");
			var field = typeof(OpCodes).GetField(normalized, ReflectionBindingFlags.Public | ReflectionBindingFlags.Static | ReflectionBindingFlags.IgnoreCase);
			if (field == null)
				throw new ArgumentException($"Unknown OpCode '{opName}' on line {index}.");

			var label = match.Groups["label"].Success ? match.Groups["label"].Value.Trim() : null;
			return new ParsedInstructionSpec(label, (OpCode)field.GetValue(null)!, match.Groups["operand"].Success ? match.Groups["operand"].Value.Trim() : null);
		}

		object? CreateOperandForInstruction(
			MethodDef targetMethod,
			ParsedInstructionSpec spec,
			IReadOnlyDictionary<string, Instruction> labelMap,
			IReadOnlyList<Instruction> originalInstructions,
			bool allowOriginalInstructionTargets) {
			switch (spec.OpCode.OperandType) {
			case OperandType.InlineNone:
				return null;

			case OperandType.ShortInlineI:
				if (spec.OpCode.Code == Code.Ldc_I4_S)
					return sbyte.Parse(RequireOperand(spec), CultureInfo.InvariantCulture);
				return byte.Parse(RequireOperand(spec), CultureInfo.InvariantCulture);

			case OperandType.InlineI:
				return int.Parse(RequireOperand(spec), CultureInfo.InvariantCulture);

			case OperandType.InlineI8:
				return long.Parse(RequireOperand(spec), CultureInfo.InvariantCulture);

			case OperandType.ShortInlineR:
				return float.Parse(RequireOperand(spec), CultureInfo.InvariantCulture);

			case OperandType.InlineR:
				return double.Parse(RequireOperand(spec), CultureInfo.InvariantCulture);

			case OperandType.InlineString:
				return RequireOperand(spec);

			case OperandType.InlineField:
				return ResolveFieldReference(targetMethod.Module, RequireOperand(spec));

			case OperandType.InlineMethod:
				return ResolveMethodReference(targetMethod.Module, RequireOperand(spec));

			case OperandType.InlineType:
				return ResolveTypeReference(targetMethod.Module, RequireOperand(spec));

			case OperandType.InlineTok: {
				var raw = RequireOperand(spec);
				if (raw.Contains("::", StringComparison.Ordinal)) {
					if (raw.Contains("(", StringComparison.Ordinal))
						return ResolveMethodReference(targetMethod.Module, raw);
					return ResolveFieldReference(targetMethod.Module, raw);
				}
				return ResolveTypeReference(targetMethod.Module, raw);
			}

			case OperandType.InlineVar:
			case OperandType.ShortInlineVar:
				return ResolveVariableOperand(targetMethod, spec.OpCode, RequireOperand(spec));

			case OperandType.InlineBrTarget:
			case OperandType.ShortInlineBrTarget:
				return ResolveBranchTarget(RequireOperand(spec), labelMap, originalInstructions, allowOriginalInstructionTargets);

			case OperandType.InlineSwitch:
				return ResolveSwitchTargets(RequireOperand(spec), labelMap, originalInstructions, allowOriginalInstructionTargets);

			default:
				throw new ArgumentException($"Opcode '{spec.OpCode.Name}' uses unsupported operand type '{spec.OpCode.OperandType}'.");
			}
		}

		Instruction ResolveBranchTarget(
			string operandText,
			IReadOnlyDictionary<string, Instruction> labelMap,
			IReadOnlyList<Instruction> originalInstructions,
			bool allowOriginalInstructionTargets) {
			var token = operandText.Trim();
			if (labelMap.TryGetValue(token, out var labeledInstruction))
				return labeledInstruction;

			if (!allowOriginalInstructionTargets)
				throw new ArgumentException($"Branch target '{operandText}' must resolve to a label inside the replacement IL block.");

			if (token.StartsWith("line:", StringComparison.OrdinalIgnoreCase)) {
				var indexText = token.Substring(5);
				if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineIndex))
					throw new ArgumentException($"Branch target '{operandText}' has an invalid line reference.");
				if (lineIndex < 0 || lineIndex >= originalInstructions.Count)
					throw new ArgumentException($"Branch target '{operandText}' is out of range. Valid original line indexes: 0-{Math.Max(0, originalInstructions.Count - 1)}.");
				return originalInstructions[lineIndex];
			}

			if (token.StartsWith("IL_", StringComparison.OrdinalIgnoreCase)) {
				var offsetText = token.Substring(3);
				if (!int.TryParse(offsetText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset))
					throw new ArgumentException($"Branch target '{operandText}' has an invalid IL offset.");
				var target = originalInstructions.FirstOrDefault(instr => instr.Offset == offset);
				return target ?? throw new ArgumentException($"No original instruction with offset IL_{offset:X4} was found.");
			}

			if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericLine)) {
				if (numericLine < 0 || numericLine >= originalInstructions.Count)
					throw new ArgumentException($"Branch target '{operandText}' is out of range. Valid original line indexes: 0-{Math.Max(0, originalInstructions.Count - 1)}.");
				return originalInstructions[numericLine];
			}

			throw new ArgumentException($"Branch target '{operandText}' could not be resolved. Use a label, line:<index>, IL_<offset>, or a 0-based original line index.");
		}

		Instruction[] ResolveSwitchTargets(
			string operandText,
			IReadOnlyDictionary<string, Instruction> labelMap,
			IReadOnlyList<Instruction> originalInstructions,
			bool allowOriginalInstructionTargets) {
			var tokens = operandText.Split(',')
				.Select(token => token.Trim())
				.Where(token => token.Length > 0)
				.ToArray();
			if (tokens.Length == 0)
				throw new ArgumentException("switch requires at least one target.");

			return tokens.Select(token => ResolveBranchTarget(token, labelMap, originalInstructions, allowOriginalInstructionTargets)).ToArray();
		}

		static Instruction CreateInstruction(OpCode opCode, object? operand) {
			if (operand == null)
				return Instruction.Create(opCode);
			return operand switch {
				string s => Instruction.Create(opCode, s),
				sbyte sb => Instruction.Create(opCode, sb),
				byte b => Instruction.Create(opCode, b),
				int i => Instruction.Create(opCode, i),
				long l => Instruction.Create(opCode, l),
				float f => Instruction.Create(opCode, f),
				double d => Instruction.Create(opCode, d),
				IField field => Instruction.Create(opCode, field),
				IMethod method => Instruction.Create(opCode, method),
				ITypeDefOrRef type => Instruction.Create(opCode, type),
				Local local => Instruction.Create(opCode, local),
				Parameter parameter => Instruction.Create(opCode, parameter),
				Instruction target => Instruction.Create(opCode, target),
				Instruction[] targets => Instruction.Create(opCode, targets),
				_ => throw new ArgumentException($"Unsupported operand type '{operand.GetType().FullName}' for opcode '{opCode.Name}'.")
			};
		}

		object ResolveVariableOperand(MethodDef targetMethod, OpCode opCode, string operandText) {
			var normalized = operandText.Trim();
			var prefersArgument = opCode.Name.StartsWith("ldarg", StringComparison.OrdinalIgnoreCase) ||
				opCode.Name.StartsWith("starg", StringComparison.OrdinalIgnoreCase);
			var prefersLocal = opCode.Name.StartsWith("ldloc", StringComparison.OrdinalIgnoreCase) ||
				opCode.Name.StartsWith("stloc", StringComparison.OrdinalIgnoreCase);

			if (normalized.StartsWith("arg:", StringComparison.OrdinalIgnoreCase))
				return ResolveParameter(targetMethod, normalized.Substring(4));
			if (normalized.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
				return ResolveLocal(targetMethod, normalized.Substring(6));
			if (prefersArgument)
				return ResolveParameter(targetMethod, normalized);
			if (prefersLocal)
				return ResolveLocal(targetMethod, normalized);

			if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
				return ResolveLocal(targetMethod, normalized);

			throw new ArgumentException($"Unable to resolve variable operand '{operandText}'. Use arg:<index|name> or local:<index|name>.");
		}

		HashSet<Instruction> GetRemovedOriginalInstructions(string mode, int lineNumber, IReadOnlyList<Instruction> originalInstructions, int insertedInstructionCount) {
			switch (mode) {
			case "overwrite":
				return new HashSet<Instruction>(originalInstructions.Skip(lineNumber).Take(Math.Min(insertedInstructionCount, Math.Max(0, originalInstructions.Count - lineNumber))));
			case "replace_all":
				return new HashSet<Instruction>(originalInstructions);
			default:
				return new HashSet<Instruction>();
			}
		}

		void EnsureReferencedInstructionsRemain(IEnumerable<Instruction> instructions, HashSet<Instruction> removedOriginalInstructions) {
			if (removedOriginalInstructions.Count == 0)
				return;

			foreach (var instruction in instructions) {
				if (instruction.Operand is Instruction target && removedOriginalInstructions.Contains(target))
					throw new ArgumentException($"Instruction '{instruction.OpCode.Name}' targets an original instruction that would be removed by this edit. Use a label or a surviving target.");
				if (instruction.Operand is Instruction[] targets && targets.Any(removedOriginalInstructions.Contains))
					throw new ArgumentException($"Instruction '{instruction.OpCode.Name}' targets at least one original instruction that would be removed by this edit. Use labels or surviving targets.");
			}
		}

		Parameter ResolveParameter(MethodDef method, string token) {
			if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) {
				var parameter = method.Parameters.FirstOrDefault(p => p.MethodSigIndex == index);
				if (parameter != null)
					return parameter;
			}

			var byName = method.Parameters.FirstOrDefault(p => string.Equals(p.Name, token, StringComparison.OrdinalIgnoreCase));
			if (byName != null)
				return byName;

			throw new ArgumentException($"Parameter '{token}' was not found in method '{method.FullName}'.");
		}

		Local ResolveLocal(MethodDef method, string token) {
			if (method.Body == null)
				throw new ArgumentException($"Method '{method.FullName}' has no body and therefore no locals.");

			if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) {
				if (index >= 0 && index < method.Body.Variables.Count)
					return method.Body.Variables[index];
			}

			var byName = method.Body.Variables.FirstOrDefault(l => string.Equals(l.Name, token, StringComparison.OrdinalIgnoreCase));
			if (byName != null)
				return byName;

			throw new ArgumentException($"Local '{token}' was not found in method '{method.FullName}'.");
		}

		ITypeDefOrRef ResolveTypeReference(ModuleDef targetModule, string rawTypeName) {
			var typeName = NormalizeTypeName(rawTypeName);

			var loadedType = documentTreeView.GetAllModuleNodes()
				.SelectMany(n => GetAllTypesRecursive(n.GetModule().Types))
				.FirstOrDefault(t => NormalizeTypeName(t.FullName) == typeName || NormalizeTypeName(t.Name.String) == typeName);
			if (loadedType != null)
				return targetModule.Import(loadedType);

			var reflectionType = Type.GetType(rawTypeName, throwOnError: false);
			if (reflectionType != null)
				return targetModule.Import(reflectionType);

			throw new ArgumentException($"Type reference '{rawTypeName}' could not be resolved.");
		}

		IMethod ResolveMethodReference(ModuleDef targetModule, string rawMethodSpec) {
			var match = methodSpecRegex.Match(rawMethodSpec);
			if (!match.Success)
				throw new ArgumentException($"Method operand '{rawMethodSpec}' must look like Namespace.Type::Method(System.String, System.Int32).");

			var typeName = match.Groups["type"].Value.Trim();
			var methodName = match.Groups["method"].Value.Trim();
			var parameterSpecs = SplitParameterList(match.Groups["params"].Value).Select(NormalizeTypeName).ToArray();

			var loadedType = documentTreeView.GetAllModuleNodes()
				.SelectMany(n => GetAllTypesRecursive(n.GetModule().Types))
				.FirstOrDefault(t => NormalizeTypeName(t.FullName) == NormalizeTypeName(typeName) || NormalizeTypeName(t.Name.String) == NormalizeTypeName(typeName));
			if (loadedType != null) {
				var method = loadedType.Methods
					.Where(m => string.Equals(m.Name.String, methodName, StringComparison.OrdinalIgnoreCase))
					.FirstOrDefault(m => ParametersMatch(m.Parameters.Where(p => p.IsNormalMethodParameter).Select(p => NormalizeTypeName(p.Type.FullName)).ToArray(), parameterSpecs))
					?? loadedType.Methods.FirstOrDefault(m => string.Equals(m.Name.String, methodName, StringComparison.OrdinalIgnoreCase));
				if (method != null)
					return targetModule.Import(method);
			}

			var reflectionType = Type.GetType(typeName, throwOnError: false);
			if (reflectionType != null) {
				var methods = reflectionType.GetMethods(ReflectionBindingFlags.Public | ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Static | ReflectionBindingFlags.Instance)
					.Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
					.ToList();
				var selected = methods.FirstOrDefault(m => ParametersMatch(m.GetParameters().Select(p => NormalizeTypeName(p.ParameterType.FullName ?? p.ParameterType.Name)).ToArray(), parameterSpecs))
					?? methods.FirstOrDefault();
				if (selected != null)
					return targetModule.Import(selected);
			}

			throw new ArgumentException($"Method reference '{rawMethodSpec}' could not be resolved.");
		}

		IField ResolveFieldReference(ModuleDef targetModule, string rawFieldSpec) {
			var match = fieldSpecRegex.Match(rawFieldSpec);
			if (!match.Success)
				throw new ArgumentException($"Field operand '{rawFieldSpec}' must look like Namespace.Type::FieldName.");

			var typeName = match.Groups["type"].Value.Trim();
			var fieldName = match.Groups["field"].Value.Trim();

			var loadedType = documentTreeView.GetAllModuleNodes()
				.SelectMany(n => GetAllTypesRecursive(n.GetModule().Types))
				.FirstOrDefault(t => NormalizeTypeName(t.FullName) == NormalizeTypeName(typeName) || NormalizeTypeName(t.Name.String) == NormalizeTypeName(typeName));
			if (loadedType != null) {
				var field = loadedType.Fields.FirstOrDefault(f => string.Equals(f.Name.String, fieldName, StringComparison.OrdinalIgnoreCase));
				if (field != null)
					return targetModule.Import(field);
			}

			var reflectionType = Type.GetType(typeName, throwOnError: false);
			if (reflectionType != null) {
				var field = reflectionType.GetField(fieldName, ReflectionBindingFlags.Public | ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Static | ReflectionBindingFlags.Instance);
				if (field != null)
					return targetModule.Import(field);
			}

			throw new ArgumentException($"Field reference '{rawFieldSpec}' could not be resolved.");
		}

		void RefreshTree() {
			try {
				documentTreeView.TreeView.RefreshAllNodes();
			}
			catch {
			}
		}

		static string GetParameterModifier(Parameter parameter) {
			if (parameter.Type is ByRefSig)
				return parameter.ParamDef?.IsOut == true ? "out " : "ref ";
			return string.Empty;
		}

		static string ToCSharpTypeName(TypeSig type, MethodDef contextMethod) {
			switch (type.RemovePinnedAndModifiers().ElementType) {
			case ElementType.Void: return "void";
			case ElementType.Boolean: return "bool";
			case ElementType.Char: return "char";
			case ElementType.I1: return "sbyte";
			case ElementType.U1: return "byte";
			case ElementType.I2: return "short";
			case ElementType.U2: return "ushort";
			case ElementType.I4: return "int";
			case ElementType.U4: return "uint";
			case ElementType.I8: return "long";
			case ElementType.U8: return "ulong";
			case ElementType.R4: return "float";
			case ElementType.R8: return "double";
			case ElementType.String: return "string";
			case ElementType.Object: return "object";
			}

			if (type is ByRefSig byRefSig)
				return ToCSharpTypeName(byRefSig.Next, contextMethod);
			if (type is SZArraySig szArraySig)
				return $"{ToCSharpTypeName(szArraySig.Next, contextMethod)}[]";
			if (type is ArraySig arraySig)
				return $"{ToCSharpTypeName(arraySig.Next, contextMethod)}[{new string(',', Math.Max(0, (int)arraySig.Rank - 1))}]";
			if (type is PtrSig ptrSig)
				return $"{ToCSharpTypeName(ptrSig.Next, contextMethod)}*";
			if (type is GenericVar genericVar)
				return contextMethod.DeclaringType.GenericParameters[(int)genericVar.Number].Name.String;
			if (type is GenericMVar genericMVar)
				return contextMethod.GenericParameters[(int)genericMVar.Number].Name.String;
			if (type is GenericInstSig genericInstSig) {
				var genericName = CleanTypeName(genericInstSig.GenericType.TypeName);
				var genericNamespace = genericInstSig.GenericType.ReflectionNamespace;
				var fullName = string.IsNullOrWhiteSpace(genericNamespace) ? genericName : $"{genericNamespace}.{genericName}";
				return $"{fullName}<{string.Join(", ", genericInstSig.GenericArguments.Select(a => ToCSharpTypeName(a, contextMethod)))}>";
			}

			if (type is TypeDefOrRefSig tdorSig)
				return CleanTypeName(tdorSig.TypeDefOrRef.FullName);

			return CleanTypeName(type.FullName);
		}

		static string CleanTypeName(string name) =>
			name.Replace("/", ".").Replace("+", ".");

		static string SanitizeIdentifier(string identifier) {
			if (string.IsNullOrWhiteSpace(identifier))
				return "arg";

			var sb = new StringBuilder();
			if (!char.IsLetter(identifier[0]) && identifier[0] != '_')
				sb.Append('_');

			foreach (var ch in identifier) {
				sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
			}

			var sanitized = sb.ToString();
			return sanitized switch {
				"class" or "namespace" or "string" or "int" or "params" or "ref" or "out" or "base" or "this" or "event" or "operator" => "@" + sanitized,
				_ => sanitized
			};
		}

		static string[] SplitParameterList(string raw) {
			if (string.IsNullOrWhiteSpace(raw))
				return Array.Empty<string>();
			return raw.Split(',')
				.Select(s => s.Trim())
				.Where(s => !string.IsNullOrWhiteSpace(s))
				.ToArray();
		}

		static bool ParametersMatch(string[] candidate, string[] requested) {
			if (requested.Length == 0)
				return true;
			if (candidate.Length != requested.Length)
				return false;
			for (int i = 0; i < candidate.Length; i++) {
				if (NormalizeTypeName(candidate[i]) != NormalizeTypeName(requested[i]))
					return false;
			}
			return true;
		}

		static string NormalizeTypeName(string raw) {
			var value = (raw ?? string.Empty).Trim();
			if (string.IsNullOrEmpty(value))
				return string.Empty;

			value = value
				.Replace("global::", string.Empty)
				.Replace("/", ".")
				.Replace("+", ".")
				.Replace(" ", string.Empty);

			value = ReplaceTypeAlias(value, "bool", "System.Boolean");
			value = ReplaceTypeAlias(value, "byte", "System.Byte");
			value = ReplaceTypeAlias(value, "sbyte", "System.SByte");
			value = ReplaceTypeAlias(value, "short", "System.Int16");
			value = ReplaceTypeAlias(value, "ushort", "System.UInt16");
			value = ReplaceTypeAlias(value, "int", "System.Int32");
			value = ReplaceTypeAlias(value, "uint", "System.UInt32");
			value = ReplaceTypeAlias(value, "long", "System.Int64");
			value = ReplaceTypeAlias(value, "ulong", "System.UInt64");
			value = ReplaceTypeAlias(value, "float", "System.Single");
			value = ReplaceTypeAlias(value, "double", "System.Double");
			value = ReplaceTypeAlias(value, "string", "System.String");
			value = ReplaceTypeAlias(value, "object", "System.Object");
			value = ReplaceTypeAlias(value, "char", "System.Char");
			value = ReplaceTypeAlias(value, "void", "System.Void");
			return value.ToLowerInvariant();
		}

		static string ReplaceTypeAlias(string input, string alias, string fullName) =>
			string.Equals(input, alias, StringComparison.Ordinal) ? fullName : input;

		static string RequireOperand(ParsedInstructionSpec spec) =>
			!string.IsNullOrWhiteSpace(spec.OperandText)
				? spec.OperandText!
				: throw new ArgumentException($"Opcode '{spec.OpCode.Name}' requires an operand.");

		string GetRequiredString(Dictionary<string, object>? arguments, params string[] keys) {
			var value = GetOptionalString(arguments, keys);
			if (string.IsNullOrWhiteSpace(value))
				throw new ArgumentException($"{keys[0]} is required");
			return value!;
		}

		string? GetOptionalString(Dictionary<string, object>? arguments, params string[] keys) {
			if (arguments == null)
				return null;
			foreach (var key in keys) {
				if (!arguments.TryGetValue(key, out var raw) || raw == null)
					continue;
				if (raw is JsonElement element) {
					if (element.ValueKind == JsonValueKind.String)
						return element.GetString();
					if (element.ValueKind != JsonValueKind.Null && element.ValueKind != JsonValueKind.Undefined)
						return element.ToString();
				}
				else {
					return raw.ToString();
				}
			}
			return null;
		}

		int? GetOptionalInt(Dictionary<string, object>? arguments, params string[] keys) {
			if (arguments == null)
				return null;
			foreach (var key in keys) {
				if (!arguments.TryGetValue(key, out var raw) || raw == null)
					continue;
				if (raw is JsonElement element) {
					if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n))
						return n;
					if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
						return n;
				}
				else if (raw is int i) {
					return i;
				}
				else if (int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) {
					return parsed;
				}
			}
			return null;
		}

		string[] ReadStringArray(Dictionary<string, object>? arguments, params string[] keys) {
			if (arguments == null)
				return Array.Empty<string>();
			foreach (var key in keys) {
				if (!arguments.TryGetValue(key, out var raw) || raw == null)
					continue;

				if (raw is JsonElement element) {
					if (element.ValueKind == JsonValueKind.Array) {
						return element.EnumerateArray()
							.Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
							.ToArray();
					}
					if (element.ValueKind == JsonValueKind.String) {
						var single = element.GetString();
						return single == null ? Array.Empty<string>() : new[] { single };
					}
				}

				if (raw is string s)
					return new[] { s };
				if (raw is IEnumerable<string> enumerable)
					return enumerable.ToArray();
				if (raw is System.Collections.IEnumerable nonGeneric) {
					var values = new List<string>();
					foreach (var item in nonGeneric)
						values.Add(item?.ToString() ?? string.Empty);
					return values.ToArray();
				}
			}

			return Array.Empty<string>();
		}

		AssemblyDef? FindAssemblyByName(string name, string? filePath = null) {
			if (!string.IsNullOrEmpty(filePath)) {
				var normalized = filePath!.Replace('/', '\\');
				var byPath = documentTreeView.GetAllModuleNodes()
					.FirstOrDefault(m => (m.Document?.Filename ?? string.Empty).Replace('/', '\\')
						.Equals(normalized, StringComparison.OrdinalIgnoreCase));
				if (byPath?.Document?.AssemblyDef != null)
					return byPath.Document.AssemblyDef;
			}

			return documentTreeView.GetAllModuleNodes()
				.Select(m => m.Document?.AssemblyDef)
				.FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
		}

		TypeDef? FindTypeInAssemblyAll(AssemblyDef assembly, string fullName) =>
			assembly.Modules
				.SelectMany(m => GetAllTypesRecursive(m.Types))
				.FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));

		static IEnumerable<TypeDef> GetAllTypesRecursive(IEnumerable<TypeDef> types) {
			foreach (var type in types) {
				yield return type;
				foreach (var nested in GetAllTypesRecursive(type.NestedTypes))
					yield return nested;
			}
		}

		static string GetOperandString(Instruction instr) {
			if (instr.Operand == null)
				return string.Empty;
			if (instr.Operand is MethodDef md)
				return md.FullName;
			if (instr.Operand is IMethod method)
				return method.FullName;
			if (instr.Operand is FieldDef fd)
				return fd.FullName;
			if (instr.Operand is IField field)
				return field.FullName;
			if (instr.Operand is TypeDef td)
				return td.FullName;
			if (instr.Operand is ITypeDefOrRef type)
				return type.FullName;
			if (instr.Operand is string s)
				return $"\"{s}\"";
			if (instr.Operand is Instruction target)
				return $"IL_{target.Offset:X4}";
			if (instr.Operand is Instruction[] targets)
				return string.Join(", ", targets.Select(t => $"IL_{t.Offset:X4}"));
			return instr.Operand.ToString() ?? string.Empty;
		}

		static uint ParseToken(string s) {
			s = s.Trim();
			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
				uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
				return hex;
			return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : 0;
		}

		static CallToolResult Success(string text) =>
			new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = text } } };

		static CallToolResult Error(string text) =>
			new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = text } },
				IsError = true
			};

		readonly struct ParsedInstructionSpec {
			public ParsedInstructionSpec(string? label, OpCode opCode, string? operandText) {
				Label = label;
				OpCode = opCode;
				OperandText = operandText;
			}

			public string? Label { get; }
			public OpCode OpCode { get; }
			public string? OperandText { get; }
		}

		readonly struct CompileReplacementResult {
			public CompileReplacementResult(TypeDef? compiledType, MethodDef? compiledMethod, IReadOnlyList<string> diagnostics) {
				CompiledType = compiledType;
				CompiledMethod = compiledMethod;
				Diagnostics = diagnostics;
			}

			public TypeDef? CompiledType { get; }
			public MethodDef? CompiledMethod { get; }
			public IReadOnlyList<string> Diagnostics { get; }
			public bool Success => CompiledType != null && CompiledMethod != null && Diagnostics.Count == 0;
		}

		sealed class PatchImportContext {
			public PatchImportContext(TypeDef sourceType, TypeDef targetType, MethodDef sourceMethod, MethodDef targetMethod) {
				SourceType = sourceType;
				TargetType = targetType;
				SourceMethod = sourceMethod;
				TargetMethod = targetMethod;
			}

			public TypeDef SourceType { get; }
			public TypeDef TargetType { get; }
			public MethodDef SourceMethod { get; }
			public MethodDef TargetMethod { get; }
		}

		sealed class MetadataReferenceComparer : IEqualityComparer<MetadataReference> {
			public bool Equals(MetadataReference? x, MetadataReference? y) =>
				string.Equals((x as PortableExecutableReference)?.FilePath, (y as PortableExecutableReference)?.FilePath, StringComparison.OrdinalIgnoreCase);

			public int GetHashCode(MetadataReference obj) =>
				StringComparer.OrdinalIgnoreCase.GetHashCode((obj as PortableExecutableReference)?.FilePath ?? string.Empty);
		}
	}
}
