﻿#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using CSharpToTypeScript.Extensions;
using CSharpToTypeScript.Models;

namespace CSharpToTypeScript
{
    public class TsGenerator
    {
        protected TsTypeFormatterCollection _typeFormatters;
        internal TypeConvertorCollection _typeConvertors;
        protected TsMemberIdentifierFormatter _memberFormatter;
        protected TsMemberTypeFormatter _memberTypeFormatter;
        protected TsTypeVisibilityFormatter _typeVisibilityFormatter;
        protected TsModuleNameFormatter _moduleNameFormatter;
        protected TsNamespaceNameFormatter _namespaceNameFormatter;
        protected IDocAppender _docAppender;
        protected HashSet<TsClass> _generatedClasses;
        protected HashSet<TsInterface> _generatedInterfaces;
        protected HashSet<TsEnum> _generatedEnums;
        protected readonly TsClassDependencyComparer TsClassComparer = new ();
        protected readonly TsInterfaceDependencyComparer TsInterfaceComparer = new();

        public IReadOnlyDictionary<Type, TsTypeFormatter> Formaters => new Dictionary<Type, TsTypeFormatter>((IDictionary<Type, TsTypeFormatter>)this._typeFormatters._formatters);

        public string IndentationString { get; set; }

        public bool GenerateConstEnums { get; set; }

        public bool EnableNamespaceInTypeScript { get; set; }

        public TsGenerator(bool enableNamespace)
        {
            EnableNamespaceInTypeScript = enableNamespace;

            this._generatedClasses = new HashSet<TsClass>();
            this._generatedInterfaces = new HashSet<TsInterface>();
            this._generatedEnums = new HashSet<TsEnum>();
            this._typeFormatters = new TsTypeFormatterCollection();
            this._typeFormatters.RegisterTypeFormatter<TsClass>((type, formatter) =>
            {
                TsClass tsClass = (TsClass)type;
                return !tsClass.GenericArguments.Any() ? tsClass.Name : tsClass.Name + "<" 
                    + string.Join(", ", tsClass.GenericArguments.Select(a => (a is not TsCollection) ? this.GetFullyQualifiedTypeName(a) : this.GetFullyQualifiedTypeName(a) + "[]")) + ">";
            });
            this._typeFormatters.RegisterTypeFormatter<TsInterface>((type, formatter) =>
            {
                TsInterface tsClass = (TsInterface)type;
                return !tsClass.GenericArguments.Any() ? tsClass.Name : tsClass.Name + "<"
                    + string.Join(", ", tsClass.GenericArguments.Select(a => (a is not TsCollection) ? this.GetFullyQualifiedTypeName(a) : this.GetFullyQualifiedTypeName(a) + "[]")) + ">";
            });
            this._typeFormatters.RegisterTypeFormatter<TsSystemType>((type, formatter) => ((TsSystemType)type).Kind.ToTypeScriptString());
            this._typeFormatters.RegisterTypeFormatter<TsCollection>((type, formatter) => this.GetTypeName(((TsCollection)type).ItemsType) ?? throw new Exception("Invalid collection: item type has no name.") );
            this._typeFormatters.RegisterTypeFormatter<TsEnum>((type, formatter) => ((TsModuleMember)type).Name);
            this._typeConvertors = new TypeConvertorCollection();
            this._docAppender = new NullDocAppender();
            this._memberFormatter = new TsMemberIdentifierFormatter(DefaultMemberFormatter);
            this._memberTypeFormatter = new TsMemberTypeFormatter(DefaultMemberTypeFormatter);
            this._typeVisibilityFormatter = new TsTypeVisibilityFormatter(DefaultTypeVisibilityFormatter);
            this._moduleNameFormatter = new TsModuleNameFormatter(DefaultModuleNameFormatter);
            this._namespaceNameFormatter = new TsNamespaceNameFormatter(DefaultNamespaceNameFormatter);
            this.IndentationString = "\t";
            this.GenerateConstEnums = true;
        }

        public static bool DefaultTypeVisibilityFormatter(TsType tsType, string? typeName) => false;

        public static string DefaultModuleNameFormatter(TsModule module) => module.Name;

        public static string DefaultNamespaceNameFormatter(TsNamespace @namespace) => @namespace.Name;

        public static string DefaultMemberFormatter(TsProperty identifier) => identifier.Name.Length > 0 ? (char.ToLower(identifier.Name[0]) + identifier.Name.Substring(1)) : identifier.Name;

        public string DefaultMemberTypeFormatter(TsProperty tsProperty, string? memberTypeName, string currentNamespaceName,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, Int32>> importNames)
        {
            if (string.IsNullOrEmpty(memberTypeName))
            {
                memberTypeName = "any";
            }

            if (tsProperty.PropertyType is TsCollection collectionType)
            {
                memberTypeName += string.Concat(Enumerable.Repeat<string>("[]", collectionType.Dimension));
            }


            if (!EnableNamespaceInTypeScript)
            {
                var index = memberTypeName.LastIndexOf('.');
                if (index > 0)
                {
                    var @namespace = memberTypeName.Substring(0, index);
                    var typeNameWithoutNamespace = memberTypeName.Substring(index + 1);

                    if (@namespace == currentNamespaceName)
                    {
                        memberTypeName = typeNameWithoutNamespace;
                    }
                    else
                    {
                        if (!importNames.TryGetValue(@namespace, out var importedNamesForNamespace))
                        {
                            throw new Exception("Cannot find imported names for namespace \"" + @namespace + "\".");
                        }

                        if (!importedNamesForNamespace.TryGetValue(typeNameWithoutNamespace, out var usageCount))
                        {
                            throw new Exception("Cannot find imported name for type \"" + typeNameWithoutNamespace + "\"");
                        }

                        memberTypeName = BuildImportName(typeNameWithoutNamespace, usageCount);
                    }
                }
            }

            if (tsProperty.IsNullable)
            {
                return memberTypeName + " | null";
            }

            return memberTypeName;
        }

        public void RegisterTypeFormatter<TFor>(TsTypeFormatter formatter) where TFor : TsType => this._typeFormatters.RegisterTypeFormatter<TFor>(formatter);

        public void RegisterTypeFormatter(TsTypeFormatter formatter) => this._typeFormatters.RegisterTypeFormatter<TsClass>(formatter);

        public void RegisterTypeConvertor<TFor>(TypeConvertor convertor) => this._typeConvertors.RegisterTypeConverter<TFor>(convertor);

        public void SetIdentifierFormatter(TsMemberIdentifierFormatter formatter) => this._memberFormatter = formatter;

        public void SetMemberTypeFormatter(TsMemberTypeFormatter formatter) => this._memberTypeFormatter = formatter;

        public void SetTypeVisibilityFormatter(TsTypeVisibilityFormatter formatter) => this._typeVisibilityFormatter = formatter;

        public void SetModuleNameFormatter(TsModuleNameFormatter formatter) => this._moduleNameFormatter = formatter;

        public void SetDocAppender(IDocAppender appender) => this._docAppender = appender;

        public IReadOnlyDictionary<string, string> Generate(TsModelBuilder tsModelBuilder) => this.Generate(tsModelBuilder, TsGeneratorOutput.Properties | TsGeneratorOutput.Enums);

        public IReadOnlyDictionary<string, string> Generate(TsModelBuilder tsModelBuilder, TsGeneratorOutput generatorOutput)
        {
            tsModelBuilder.Build();

            var results = new Dictionary<string, string>();

            if ((generatorOutput & TsGeneratorOutput.Properties) == TsGeneratorOutput.Properties || (generatorOutput & TsGeneratorOutput.Fields) == TsGeneratorOutput.Fields)
            {
                if ((generatorOutput & TsGeneratorOutput.Constants) == TsGeneratorOutput.Constants)
                    throw new InvalidOperationException("Cannot generate constants together with properties or fields");
            }

            foreach (TsNamespace @namespace in tsModelBuilder.TsModuleService.GetNamespaces().OrderBy(n => this.FormatNamespaceName(n)))
            {
                ScriptBuilder sb = new(this.IndentationString);
                var dependencies = tsModelBuilder.TsModuleService.GetDependentNamespaces(@namespace);
                this.AppendNamespace(@namespace, sb, generatorOutput, dependencies);
                results.Add(this.FormatNamespaceName(@namespace), sb.ToString());
            }

            return results;
        }

        protected virtual void AppendNamespace(
          TsNamespace @namespace,
          ScriptBuilder sb,
          TsGeneratorOutput generatorOutput,
          IReadOnlyDictionary<string, IReadOnlyCollection<TsModuleMember>> dependencies)
        {
            List<TsClass> moduleClasses = @namespace.Classes.Where(c => !this._typeConvertors.IsConvertorRegistered(c.Type) && !c.IsIgnored)
                .OrderBy(c => this.GetTypeName(c))
                .ToList();
            List<TsInterface> moduleInterfaces = @namespace.Interfaces.Where(c => !this._typeConvertors.IsConvertorRegistered(c.Type) && !c.IsIgnored)
                .OrderBy(c => this.GetTypeName(c))
                .ToList();
            List<TsEnum> moduleEnums = @namespace.Enums.Where(e => !this._typeConvertors.IsConvertorRegistered(e.Type) && !e.IsIgnored)
                .OrderBy(e => this.GetTypeName(e))
                .ToList();

            if (generatorOutput == TsGeneratorOutput.Enums && moduleEnums.Count == 0 
                || moduleEnums.Count == 0 && moduleClasses.Count == 0 && moduleInterfaces.Count == 0
                || generatorOutput == TsGeneratorOutput.Properties && !moduleClasses.Any(c => c.Fields.Any() || c.Properties.Any()) && !moduleInterfaces.Any(i => i.Properties.Any())
                || generatorOutput == TsGeneratorOutput.Constants && !moduleClasses.Any(c => c.Constants.Any()))
                return;

            string namespaceName = this.FormatNamespaceName(@namespace);
            if (EnableNamespaceInTypeScript)
            {
                sb.AppendLine(string.Format("namespace {0} {{", namespaceName));
                using (sb.IncreaseIndentation())
                {
                    AppendNamespace(sb, generatorOutput, moduleClasses, moduleInterfaces, moduleEnums, new Dictionary<string, IReadOnlyDictionary<string, Int32>>());
                }
                sb.AppendLine("}");
            }
            else
            {
                var importedNames = AppendDependencies(sb, dependencies, false);
                AppendNamespace(sb, generatorOutput, moduleClasses, moduleInterfaces, moduleEnums, importedNames);
            }
        }

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Int32>> AppendDependencies(ScriptBuilder sb,
            IReadOnlyDictionary<string, IReadOnlyCollection<TsModuleMember>> dependencies, bool addIndentation)
        {
            Dictionary<string, IReadOnlyDictionary<string, Int32>> importIndices = new();

            if (dependencies.Count > 0)
            {
                if (addIndentation)
                {
                    foreach (var dependency in dependencies)
                    {
                        var importIndicesForNamespace = new Dictionary<string, Int32>();
                        sb.AppendLineIndented("import { " + string.Join(", ", dependency.Value.Select(v => GetImportName(v.Name, importIndices, importIndicesForNamespace))) + " } from './" + dependency.Key + "';");
                    }
                }
                else
                {
                    foreach (var dependency in dependencies)
                    {
                        var importIndicesForNamespace = new Dictionary<string, Int32>();
                        sb.AppendLine("import { " + string.Join(", ", dependency.Value.Select(v => GetImportName(v.Name, importIndices, importIndicesForNamespace))) + " } from './" + dependency.Key + "';");
                        importIndices[dependency.Key] = importIndicesForNamespace;
                    }
                }

                sb.AppendLine();
            }

            return importIndices;
        }

        private static string GetImportName(string name, Dictionary<string, IReadOnlyDictionary<string, int>> importIndices, Dictionary<string, Int32> currentIndices)
        {
            Int32 usageCount = 0;
            
            foreach (var importIndicesForNamespace in importIndices.Values)
            {
                usageCount += importIndicesForNamespace.Keys.Count(k => k == name);
            }

            currentIndices[name] = usageCount;

            return name + " as " + BuildImportName(name, usageCount);
        }

        private void AppendNamespace(ScriptBuilder sb, TsGeneratorOutput generatorOutput, List<TsClass> moduleClasses, List<TsInterface> moduleInterfaces,
            List<TsEnum> moduleEnums, IReadOnlyDictionary<string, IReadOnlyDictionary<string, Int32>> importNames)
        {
            if ((generatorOutput & TsGeneratorOutput.Constants) == TsGeneratorOutput.Constants)
            {
                foreach (TsClass classModel in moduleClasses)
                {
                    if (classModel.Ignore == null)
                        this.AppendConstantModule(classModel, sb, importNames);
                }
            }
            if ((generatorOutput & TsGeneratorOutput.Enums) == TsGeneratorOutput.Enums)
            {
                foreach (TsEnum enumModel in moduleEnums)
                    this.AppendEnumDefinition(enumModel, sb, generatorOutput);
            }
            if ((generatorOutput & TsGeneratorOutput.Properties) == TsGeneratorOutput.Properties || (generatorOutput & TsGeneratorOutput.Fields) == TsGeneratorOutput.Fields)
            {
                foreach (TsInterface interfaceModel in moduleInterfaces.OrderBy(i => i, TsInterfaceComparer).ThenBy(i => this.GetTypeName(i)))
                    this.AppendInterfaceDefinition(interfaceModel, sb, generatorOutput, importNames);

                foreach (TsClass classModel in moduleClasses.OrderBy(c => c, TsClassComparer).ThenBy(c => this.GetTypeName(c)))
                    this.AppendClassDefinition(classModel, sb, generatorOutput, importNames);
            }
        }

        protected virtual void AppendClassDefinition(
            TsClass classModel,
            ScriptBuilder sb,
            TsGeneratorOutput generatorOutput,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, Int32>> importNames)
        {
            string? typeName = this.GetTypeName(classModel);
            string str = this.GetTypeVisibility(classModel, typeName) ? "export " : "";
            this._docAppender.AppendClassDoc(sb, classModel, typeName);
            sb.AppendFormatIndented("{0}class {1}", str, typeName);
            if (classModel.BaseType != null)
            {
                if (classModel.BaseType is TsInterface)
                {
                    sb.AppendFormat(" implements {0}", FormatTypeName(classModel.NamespaceName, classModel.BaseType, importNames));
                }
                else
                {
                    sb.AppendFormat(" extends {0}", FormatTypeName(classModel.NamespaceName, classModel.BaseType, importNames));
                }
            }

            if (classModel.Interfaces.Count > 0)
            {
                string?[] array = classModel.Interfaces.Select(t => FormatTypeName(classModel.NamespaceName, t, importNames)).ToArray();
                string format = classModel.BaseType is TsInterface ? ", {0}" : " implements {0}";
                sb.AppendFormat(format, string.Join(", ", array));
            }

            sb.AppendLine(" {");

            List<TsProperty> source = new ();
            if ((generatorOutput & TsGeneratorOutput.Properties) == TsGeneratorOutput.Properties)
                source.AddRange(classModel.Properties);
            if ((generatorOutput & TsGeneratorOutput.Fields) == TsGeneratorOutput.Fields)
                source.AddRange(classModel.Fields);
            using (sb.IncreaseIndentation())
            {
                foreach (TsProperty property in source.Where(p => p.JsonIgnore == null).OrderBy(p => this.FormatPropertyNameWithOptionalModifier(p)))
                {
                    this._docAppender.AppendPropertyDoc(sb, property, this.FormatPropertyNameWithOptionalModifier(property),
                        this.FormatPropertyType(classModel.NamespaceName, property, importNames));
                    sb.AppendLineIndented(string.Format("{0}: {1};", this.FormatPropertyNameWithOptionalModifier(property),
                        this.FormatPropertyType(classModel.NamespaceName, property, importNames)));
                }
            }
            sb.AppendLineIndented("}");
            this._generatedClasses.Add(classModel);
        }

        private string FormatTypeName(string namespaceName, TsType type, IReadOnlyDictionary<string, IReadOnlyDictionary<string, Int32>> importNames)
        {
            var typeName = GetFullyQualifiedTypeName(type) ?? throw new ArgumentException("Type name cannot be blank.", nameof(type));

            if (!EnableNamespaceInTypeScript)
            {
                var index = typeName.LastIndexOf('.');
                if (index > 0)
                {
                    var @namespace = typeName.Substring(0, index);
                    var typeNameWithoutNamespace = typeName.Substring(index + 1);

                    if (@namespace == namespaceName)
                    {
                        return typeNameWithoutNamespace;
                    }

                    if (!importNames.TryGetValue(@namespace, out var importedNamesForNamespace))
                    {
                        throw new Exception("Cannot find imported names for namespace \"" + @namespace + "\".");
                    }

                    if (!importedNamesForNamespace.TryGetValue(typeNameWithoutNamespace, out var usageCount))
                    {
                        throw new Exception("Cannot find imported name for type \"" + typeNameWithoutNamespace + "\"");
                    }

                    return BuildImportName(typeNameWithoutNamespace, usageCount);
                }
            }

            if (typeName.StartsWith(namespaceName + '.'))
            {
                return typeName.Substring(namespaceName.Length + 1);
            }

            return typeName;
        }

        private static string BuildImportName(string typeName, int usageCount)
        {
            if (typeName.Length < 1)
            {
                throw new Exception("Type name cannot be blank.");
            }

            if (usageCount == 0)
            {
                return "imp" + char.ToUpper(typeName[0]) + typeName.Substring(1);
            }

            return "imp" + char.ToUpper(typeName[0]) + typeName.Substring(1) + usageCount;
        }

        protected virtual void AppendInterfaceDefinition(
          TsInterface interfaceModel,
          ScriptBuilder sb,
          TsGeneratorOutput generatorOutput,
          IReadOnlyDictionary<string, IReadOnlyDictionary<string, Int32>> importNames)
        {
            string? typeName = this.GetTypeName(interfaceModel);
            string str = this.GetTypeVisibility(interfaceModel, typeName) ? "export " : "";
            this._docAppender.AppendInterfaceDoc(sb, interfaceModel, typeName);
            sb.AppendFormatIndented("{0}interface {1}", str, typeName);
            if (interfaceModel.BaseType != null)
                sb.AppendFormat(" extends {0}", FormatTypeName(interfaceModel.NamespaceName, interfaceModel.BaseType, importNames));
            if (interfaceModel.Interfaces.Count > 0)
            {
                string?[] array = interfaceModel.Interfaces.Select(t => FormatTypeName(interfaceModel.NamespaceName, t, importNames)).ToArray();
                string format = interfaceModel.Type.IsInterface ? " extends {0}" : (interfaceModel.BaseType != null ? " , {0}" : " extends {0}");
                sb.AppendFormat(format, string.Join(" ,", array));
            }
            sb.AppendLine(" {");
            List<TsProperty> source = new();
            if ((generatorOutput & TsGeneratorOutput.Properties) == TsGeneratorOutput.Properties)
                source.AddRange(interfaceModel.Properties);
            using (sb.IncreaseIndentation())
            {
                foreach (TsProperty property in source.Where(p => p.JsonIgnore == null).OrderBy(p => this.FormatPropertyNameWithOptionalModifier(p)))
                {
                    this._docAppender.AppendPropertyDoc(sb, property, this.FormatPropertyNameWithOptionalModifier(property),
                        FormatPropertyType(interfaceModel.NamespaceName, property, importNames));
                    sb.AppendLineIndented(string.Format("{0}: {1};", this.FormatPropertyNameWithOptionalModifier(property),
                        FormatPropertyType(interfaceModel.NamespaceName, property, importNames)));
                }
            }
            sb.AppendLineIndented("}");
            sb.AppendLine();
            this._generatedInterfaces.Add(interfaceModel);
        }

        protected virtual void AppendEnumDefinition(
          TsEnum enumModel,
          ScriptBuilder sb,
          TsGeneratorOutput output)
        {
            string? typeName = this.GetTypeName(enumModel);
            string str1 = (output & TsGeneratorOutput.Enums) == TsGeneratorOutput.Enums || (output & TsGeneratorOutput.Constants) == TsGeneratorOutput.Constants ? "export " : "";
            this._docAppender.AppendEnumDoc(sb, enumModel, typeName);
            string str2 = this.GenerateConstEnums ? "const " : string.Empty;
            sb.AppendLineIndented(string.Format("{0}{2}enum {1} {{", str1, typeName, str2));
            using (sb.IncreaseIndentation())
            {
                int num = 1;
                foreach (TsEnumValue tsEnumValue in (IEnumerable<TsEnumValue>)enumModel.Values)
                {
                    this._docAppender.AppendEnumValueDoc(sb, tsEnumValue);
                    sb.AppendLineIndented(string.Format(num < enumModel.Values.Count ? "{0} = {1}," : "{0} = {1}", tsEnumValue.Name, tsEnumValue.Value));
                    ++num;
                }
            }
            sb.AppendLineIndented("}");
            sb.AppendLine();
            this._generatedEnums.Add(enumModel);
        }

        protected virtual void AppendConstantModule(TsClass classModel, ScriptBuilder sb,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, Int32>> importNames)
        {
            if (!classModel.Constants.Any<TsProperty>())
                return;
            string? typeName = this.GetTypeName(classModel);
            sb.AppendLineIndented(string.Format("export namespace {0} {{", typeName));
            using (sb.IncreaseIndentation())
            {
                foreach (TsProperty constant in (IEnumerable<TsProperty>)classModel.Constants)
                {
                    if (constant.JsonIgnore == null)
                    {
                        this._docAppender.AppendConstantModuleDoc(sb, constant, this.FormatPropertyNameWithOptionalModifier(constant),
                            FormatPropertyType(classModel.NamespaceName, constant, importNames));
                        sb.AppendFormatIndented("export const {0}: {1} = {2};", this.FormatPropertyNameWithOptionalModifier(constant),
                            FormatPropertyType(classModel.NamespaceName, constant, importNames), GetPropertyConstantValue(constant));
                        sb.AppendLine();
                    }
                }
            }
            sb.AppendLineIndented("}");
            this._generatedClasses.Add(classModel);
        }

        public string FormatNamespaceName(TsNamespace @namespace) => this._namespaceNameFormatter(@namespace);

        public string FormatPropertyNameWithOptionalModifier(TsProperty property)
        {
            string propertyName = this._memberFormatter(property);
            if (!property.IsRequired)
                propertyName += "?";
            return propertyName;
        }

        public string FormatPropertyName(TsProperty property)
        {
            return this._memberFormatter(property);
        }

        public string FormatPropertyType(string currentNamespaceName, TsProperty property,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, Int32>> importNames)
        {
            string? qualifiedTypeName = this.GetFullyQualifiedTypeName(property.PropertyType);

            var typeName = this._memberTypeFormatter(property, qualifiedTypeName, currentNamespaceName, importNames);

            if (string.IsNullOrEmpty(typeName))
            {
                return "any";
            }

            if (typeName.StartsWith(currentNamespaceName + '.'))
            {
                return typeName.Substring(currentNamespaceName.Length + 1);
            }

            return typeName;
        }

        public string? GetFullyQualifiedTypeName(TsType? type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            string namespaceName = string.Empty;
            if (type is TsModuleMember tsModuleMember && !this._typeConvertors.IsConvertorRegistered(type.Type))
            {
                namespaceName = (tsModuleMember.Namespace != null) ? this.FormatNamespaceName(tsModuleMember.Namespace) 
                    : string.Empty;
            }
            else if (type is TsCollection tsCollection)
            {
                namespaceName = this.GetCollectionModuleName(tsCollection);
            } 

            return type.Type.IsGenericParameter || string.IsNullOrEmpty(namespaceName) ? this.GetTypeName(type) : namespaceName + "." + this.GetTypeName(type);
        }

        public string GetCollectionModuleName(TsCollection collectionType)
        {
            if (collectionType.ItemsType is TsModuleMember tsModuleMember
                && !this._typeConvertors.IsConvertorRegistered(collectionType.ItemsType.Type)
                && !collectionType.ItemsType.Type.IsGenericParameter)
            {
                return tsModuleMember.Namespace != null ? this.FormatNamespaceName(tsModuleMember.Namespace) : string.Empty;
            }
            else if (collectionType.ItemsType is TsCollection tsCollection)
            {
                return this.GetCollectionModuleName(tsCollection);
            }

            return string.Empty;
        }

        public string? GetTypeName(TsType type) => this._typeConvertors.IsConvertorRegistered(type.Type) ? this._typeConvertors.ConvertType(type.Type)
            : this._typeFormatters.FormatType(type);

        public static string GetPropertyConstantValue(TsProperty property)
        {
            string str = property.PropertyType.Type == typeof(string) ? "\"" : "";
            return str + property.ConstantValue?.ToString() + str;
        }

        public bool GetTypeVisibility(TsType tsType, string? typeName) => this._typeVisibilityFormatter(tsType, typeName);
    }
}
