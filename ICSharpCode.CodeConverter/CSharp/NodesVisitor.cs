﻿using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.CodeConverter.Util;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ArgumentListSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentListSyntax;
using ArgumentSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax;
using ArrayRankSpecifierSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ArrayRankSpecifierSyntax;
using ArrayTypeSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ArrayTypeSyntax;
using AttributeListSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.AttributeListSyntax;
using CatchFilterClauseSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.CatchFilterClauseSyntax;
using EnumMemberDeclarationSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.EnumMemberDeclarationSyntax;
using ExpressionSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax;
using IdentifierNameSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax;
using InterpolatedStringContentSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.InterpolatedStringContentSyntax;
using NameSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.NameSyntax;
using ParameterListSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ParameterListSyntax;
using ParameterSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax;
using SimpleNameSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.SimpleNameSyntax;
using SyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using SyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;
using SyntaxNodeExtensions = ICSharpCode.CodeConverter.Util.SyntaxNodeExtensions;
using TypeArgumentListSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.TypeArgumentListSyntax;
using TypeParameterConstraintClauseSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.TypeParameterConstraintClauseSyntax;
using TypeParameterListSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.TypeParameterListSyntax;
using TypeParameterSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.TypeParameterSyntax;
using TypeSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax;
using VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VBasic = Microsoft.CodeAnalysis.VisualBasic;

namespace ICSharpCode.CodeConverter.CSharp
{
    public partial class VisualBasicConverter
    {
        class NodesVisitor : VBasic.VisualBasicSyntaxVisitor<CSharpSyntaxNode>
        {
            private SemanticModel semanticModel;
            private readonly Dictionary<ITypeSymbol, string> createConvertMethodsLookupByReturnType;
            private readonly Dictionary<VBSyntax.StatementSyntax, MemberDeclarationSyntax[]> additionalDeclarations = new Dictionary<VBSyntax.StatementSyntax, MemberDeclarationSyntax[]>();
            private readonly Stack<string> withBlockTempVariableNames = new Stack<string>();
            readonly IDictionary<string, string> importedNamespaces;
            public CommentConvertingNodesVisitor TriviaConvertingVisitor { get; }

            public NodesVisitor(SemanticModel semanticModel)
            {
                this.semanticModel = semanticModel;
                this.TriviaConvertingVisitor = new CommentConvertingNodesVisitor(this);
                importedNamespaces = new Dictionary<string, string> {{VBasic.VisualBasicExtensions.RootNamespace(semanticModel.Compilation).ToString(), ""}};
                this.createConvertMethodsLookupByReturnType = CreateConvertMethodsLookupByReturnType(semanticModel);
            }

            private static Dictionary<ITypeSymbol, string> CreateConvertMethodsLookupByReturnType(SemanticModel semanticModel)
            {
                var systemDotConvert = typeof(Convert).FullName;
                var convertMethods = semanticModel.Compilation.GetTypeByMetadataName(systemDotConvert).GetMembers().Where(m =>
                    m.Name.StartsWith("To", StringComparison.Ordinal) && m.GetParameters().Length == 1);
                var methodsByType = convertMethods.Where(m => m.Name != nameof(System.Convert.ToBase64String))
                    .GroupBy(m => new { ReturnType = m.GetReturnType(), Name = $"{systemDotConvert}.{m.Name}" })
                    .ToDictionary(m => m.Key.ReturnType, m => m.Key.Name);
                return methodsByType;
            }

            public override CSharpSyntaxNode DefaultVisit(SyntaxNode node)
            {
                if (CreateMethodBodyVisitor().Visit(node).Any()) {
                    throw new NotImplementedOrRequiresSurroundingMethodDeclaration(node.GetType() + " not implemented!");
                }
                throw new NotImplementedException(node.GetType() + " not implemented!");
            }

            public override CSharpSyntaxNode VisitGetTypeExpression(VBSyntax.GetTypeExpressionSyntax node)
            {
                return SyntaxFactory.TypeOfExpression((TypeSyntax)node.Type.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitGlobalName(VBSyntax.GlobalNameSyntax node)
            {
                return SyntaxFactory.IdentifierName(SyntaxFactory.Token(SyntaxKind.GlobalKeyword));
            }

            #region Attributes

            private SyntaxList<AttributeListSyntax> ConvertAttributes(SyntaxList<VBSyntax.AttributeListSyntax> attributeListSyntaxs)
            {
                return SyntaxFactory.List(attributeListSyntaxs.SelectMany(ConvertAttribute));
            }

            IEnumerable<AttributeListSyntax> ConvertAttribute(VBSyntax.AttributeListSyntax attributeList)
            {
                return attributeList.Attributes.Select(a => (AttributeListSyntax)a.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitAttribute(VBSyntax.AttributeSyntax node)
            {
                return SyntaxFactory.AttributeList(
                    node.Target == null ? null : SyntaxFactory.AttributeTargetSpecifier(ConvertToken(node.Target.AttributeModifier)),
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute((NameSyntax)node.Name.Accept(TriviaConvertingVisitor), (AttributeArgumentListSyntax)node.ArgumentList?.Accept(TriviaConvertingVisitor)))
                );
            }

            #endregion

            public override CSharpSyntaxNode VisitCompilationUnit(VBSyntax.CompilationUnitSyntax node)
            {
                var options = (VBasic.VisualBasicCompilationOptions)semanticModel.Compilation.Options;
                var importsClauses = options.GlobalImports.Select(gi => gi.Clause).Concat(node.Imports.SelectMany(imp => imp.ImportsClauses)).ToList();
                foreach (var importClause in importsClauses.OfType<VBSyntax.SimpleImportsClauseSyntax>()) {
                    importedNamespaces[importClause.Name.ToString()] = importClause.Alias != null ? importClause.Alias.Identifier.ToString() : "";
                }
                
                var attributes = SyntaxFactory.List(node.Attributes.SelectMany(a => a.AttributeLists).SelectMany(ConvertAttribute));
                var members = SyntaxFactory.List(node.Members.Select(m => (MemberDeclarationSyntax)m.Accept(TriviaConvertingVisitor)));

                return SyntaxFactory.CompilationUnit(
                    SyntaxFactory.List<ExternAliasDirectiveSyntax>(),
                    SyntaxFactory.List(importsClauses.Select(c => (UsingDirectiveSyntax)c.Accept(TriviaConvertingVisitor))),
                    attributes,
                    members
                );
            }

            public override CSharpSyntaxNode VisitSimpleImportsClause(VBSyntax.SimpleImportsClauseSyntax node)
            {
                var nameEqualsSyntax = node.Alias == null ? null 
                    : SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName(ConvertIdentifier(node.Alias.Identifier, semanticModel)));
                var usingDirective = SyntaxFactory.UsingDirective(nameEqualsSyntax, (NameSyntax)node.Name.Accept(TriviaConvertingVisitor));
                return usingDirective;
            }

            public override CSharpSyntaxNode VisitNamespaceBlock(VBSyntax.NamespaceBlockSyntax node)
            {
                var members = node.Members.Select(m => (MemberDeclarationSyntax)m.Accept(TriviaConvertingVisitor));

                var namespaceDeclaration = SyntaxFactory.NamespaceDeclaration(
                    (NameSyntax)node.NamespaceStatement.Name.Accept(TriviaConvertingVisitor),
                    SyntaxFactory.List<ExternAliasDirectiveSyntax>(),
                    SyntaxFactory.List<UsingDirectiveSyntax>(),
                    SyntaxFactory.List(members)
                );

                // Add this afterwards so we don't try to shorten the namespace declaration itself
                importedNamespaces[namespaceDeclaration.Name.ToString()] = "";

                return namespaceDeclaration;
            }

            #region Namespace Members

            IEnumerable<MemberDeclarationSyntax> ConvertMembers(SyntaxList<VBSyntax.StatementSyntax> members)
            {
                foreach (var member in members) {
                    yield return (MemberDeclarationSyntax)member.Accept(TriviaConvertingVisitor);

                    if (additionalDeclarations.TryGetValue(member, out var additionalStatements)) {
                        additionalDeclarations.Remove(member);
                        foreach (var additionalStatement in additionalStatements) {
                            yield return additionalStatement;
                        }
                    }
                    
                }
            }

            public override CSharpSyntaxNode VisitClassBlock(VBSyntax.ClassBlockSyntax node)
            {
                var classStatement = node.ClassStatement;
                var attributes = ConvertAttributes(classStatement.AttributeLists);
                SplitTypeParameters(classStatement.TypeParameterList, out var parameters, out var constraints);
                var convertedIdentifier = ConvertIdentifier(classStatement.Identifier, semanticModel);

                return SyntaxFactory.ClassDeclaration(
                    attributes,
                    ConvertModifiers(classStatement.Modifiers),
                    convertedIdentifier,
                    parameters,
                    ConvertInheritsAndImplements(node.Inherits, node.Implements),
                    constraints,
                    SyntaxFactory.List(ConvertMembers(node.Members))
                    );
            }

            private BaseListSyntax ConvertInheritsAndImplements(SyntaxList<VBSyntax.InheritsStatementSyntax> inherits, SyntaxList<VBSyntax.ImplementsStatementSyntax> implements)
            {
                if (inherits.Count + implements.Count == 0)
                    return null;
                var baseTypes = new List<BaseTypeSyntax>();
                foreach (var t in inherits.SelectMany(c => c.Types).Concat(implements.SelectMany(c => c.Types)))
                    baseTypes.Add(SyntaxFactory.SimpleBaseType((TypeSyntax)t.Accept(TriviaConvertingVisitor)));
                return SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(baseTypes));
            }

            public override CSharpSyntaxNode VisitModuleBlock(VBSyntax.ModuleBlockSyntax node)
            {
                var stmt = node.ModuleStatement;
                var attributes = ConvertAttributes(stmt.AttributeLists);
                var members = SyntaxFactory.List(ConvertMembers(node.Members));

                TypeParameterListSyntax parameters;
                SyntaxList<TypeParameterConstraintClauseSyntax> constraints;
                SplitTypeParameters(stmt.TypeParameterList, out parameters, out constraints);

                return SyntaxFactory.ClassDeclaration(
                    attributes,
                    ConvertModifiers(stmt.Modifiers, TokenContext.InterfaceOrModule).Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword)),
                    ConvertIdentifier(stmt.Identifier, semanticModel),
                    parameters,
                    ConvertInheritsAndImplements(node.Inherits, node.Implements),
                    constraints,
                    members
                );
            }

            public override CSharpSyntaxNode VisitStructureBlock(VBSyntax.StructureBlockSyntax node)
            {
                var stmt = node.StructureStatement;
                var attributes = ConvertAttributes(stmt.AttributeLists);
                var members = SyntaxFactory.List(ConvertMembers(node.Members));

                TypeParameterListSyntax parameters;
                SyntaxList<TypeParameterConstraintClauseSyntax> constraints;
                SplitTypeParameters(stmt.TypeParameterList, out parameters, out constraints);

                return SyntaxFactory.StructDeclaration(
                    attributes,
                    ConvertModifiers(stmt.Modifiers, TokenContext.Global),
                    ConvertIdentifier(stmt.Identifier, semanticModel),
                    parameters,
                    ConvertInheritsAndImplements(node.Inherits, node.Implements),
                    constraints,
                    members
                );
            }

            public override CSharpSyntaxNode VisitInterfaceBlock(VBSyntax.InterfaceBlockSyntax node)
            {
                var stmt = node.InterfaceStatement;
                var attributes = ConvertAttributes(stmt.AttributeLists);
                var members = SyntaxFactory.List(ConvertMembers(node.Members));

                TypeParameterListSyntax parameters;
                SyntaxList<TypeParameterConstraintClauseSyntax> constraints;
                SplitTypeParameters(stmt.TypeParameterList, out parameters, out constraints);

                return SyntaxFactory.InterfaceDeclaration(
                    attributes,
                    ConvertModifiers(stmt.Modifiers, TokenContext.InterfaceOrModule),
                    ConvertIdentifier(stmt.Identifier, semanticModel),
                    parameters,
                    ConvertInheritsAndImplements(node.Inherits, node.Implements),
                    constraints,
                    members
                );
            }

            public override CSharpSyntaxNode VisitEnumBlock(VBSyntax.EnumBlockSyntax node)
            {
                var stmt = node.EnumStatement;
                // we can cast to SimpleAsClause because other types make no sense as enum-type.
                var asClause = (VBSyntax.SimpleAsClauseSyntax)stmt.UnderlyingType;
                var attributes = stmt.AttributeLists.SelectMany(ConvertAttribute);
                BaseListSyntax baseList = null;
                if (asClause != null) {
                    baseList = SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(SyntaxFactory.SimpleBaseType((TypeSyntax)asClause.Type.Accept(TriviaConvertingVisitor))));
                    if (asClause.AttributeLists.Count > 0) {
                        attributes = attributes.Concat(
                            SyntaxFactory.AttributeList(
                                SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.ReturnKeyword)),
                                SyntaxFactory.SeparatedList(asClause.AttributeLists.SelectMany(l => ConvertAttribute(l).SelectMany(a => a.Attributes)))
                            )
                        );
                    }
                }
                var members = SyntaxFactory.SeparatedList(node.Members.Select(m => (EnumMemberDeclarationSyntax)m.Accept(TriviaConvertingVisitor)));
                return SyntaxFactory.EnumDeclaration(
                    SyntaxFactory.List(attributes),
                    ConvertModifiers(stmt.Modifiers, TokenContext.Global),
                    ConvertIdentifier(stmt.Identifier, semanticModel),
                    baseList,
                    members
                );
            }

            public override CSharpSyntaxNode VisitEnumMemberDeclaration(VBSyntax.EnumMemberDeclarationSyntax node)
            {
                var attributes = ConvertAttributes(node.AttributeLists);
                return SyntaxFactory.EnumMemberDeclaration(
                    attributes,
                    ConvertIdentifier(node.Identifier, semanticModel),
                    (EqualsValueClauseSyntax)node.Initializer?.Accept(TriviaConvertingVisitor)
                );
            }

            public override CSharpSyntaxNode VisitDelegateStatement(VBSyntax.DelegateStatementSyntax node)
            {
                var attributes = node.AttributeLists.SelectMany(ConvertAttribute);

                TypeParameterListSyntax typeParameters;
                SyntaxList<TypeParameterConstraintClauseSyntax> constraints;
                SplitTypeParameters(node.TypeParameterList, out typeParameters, out constraints);

                TypeSyntax returnType;
                var asClause = node.AsClause;
                if (asClause == null) {
                    returnType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
                } else {
                    returnType = (TypeSyntax)asClause.Type.Accept(TriviaConvertingVisitor);
                    if (asClause.AttributeLists.Count > 0) {
                        attributes = attributes.Concat(
                            SyntaxFactory.AttributeList(
                                SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.ReturnKeyword)),
                                SyntaxFactory.SeparatedList(asClause.AttributeLists.SelectMany(l => ConvertAttribute(l).SelectMany(a => a.Attributes)))
                            )
                        );
                    }
                }

                return SyntaxFactory.DelegateDeclaration(
                    SyntaxFactory.List(attributes),
                    ConvertModifiers(node.Modifiers, TokenContext.Global),
                    returnType,
                    ConvertIdentifier(node.Identifier, semanticModel),
                    typeParameters,
                    (ParameterListSyntax)node.ParameterList?.Accept(TriviaConvertingVisitor),
                    constraints
                );
            }

            #endregion

            #region Type Members

            public override CSharpSyntaxNode VisitFieldDeclaration(VBSyntax.FieldDeclarationSyntax node)
            {
                var attributes = node.AttributeLists.SelectMany(ConvertAttribute);
                var unConvertableModifiers = node.Modifiers.Where(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.WithEventsKeyword)).Select(m => m.Text).ToList();
                var convertableModifiers = node.Modifiers.Where(m => !SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.WithEventsKeyword));
                var convertedModifiers = ConvertModifiers(convertableModifiers, TokenContext.VariableOrConst);
                var declarations = new List<MemberDeclarationSyntax>(node.Declarators.Count);

                foreach (var declarator in node.Declarators) {
                    foreach (var decl in SplitVariableDeclarations(declarator, this, semanticModel).Values) {
                        var baseFieldDeclarationSyntax = SyntaxFactory.FieldDeclaration(
                            SyntaxFactory.List(attributes),
                            convertedModifiers,
                            decl
                        );
                        declarations.Add(
                            unConvertableModifiers.Any()
                                ? baseFieldDeclarationSyntax.WithAppendedTrailingTrivia(
                                    SyntaxFactory.Comment(
                                        $"/* TODO ERROR didn't convert: {string.Join(",", unConvertableModifiers)} */"))
                                : baseFieldDeclarationSyntax);
                    }
                }

                additionalDeclarations.Add(node, declarations.Skip(1).ToArray());
                return declarations.First();
            }

            public override CSharpSyntaxNode VisitPropertyStatement(VBSyntax.PropertyStatementSyntax node)
            {
                bool hasBody = node.Parent is VBSyntax.PropertyBlockSyntax;
                var attributes = node.AttributeLists.SelectMany(ConvertAttribute);
                var isReadonly = node.Modifiers.Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.ReadOnlyKeyword));
                var convertibleModifiers = node.Modifiers.Where(m => !SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.ReadOnlyKeyword));
                var modifiers = ConvertModifiers(convertibleModifiers, GetMethodOrPropertyContext(node));
                var isIndexer = node.Modifiers.Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.DefaultKeyword)) && node.Identifier.ValueText.Equals("Items", StringComparison.OrdinalIgnoreCase);

                var initializer = (EqualsValueClauseSyntax)node.Initializer?.Accept(TriviaConvertingVisitor);
                var rawType = (TypeSyntax)node.AsClause?.TypeSwitch(
                    (VBSyntax.SimpleAsClauseSyntax c) => c.Type,
                    (VBSyntax.AsNewClauseSyntax c) => {
                        initializer = SyntaxFactory.EqualsValueClause((ExpressionSyntax)c.NewExpression.Accept(TriviaConvertingVisitor));
                        return VBasic.SyntaxExtensions.Type(c.NewExpression.WithoutTrivia()); // We'll end up visiting this twice so avoid trivia this time
                    },
                    _ => { throw new NotImplementedException($"{_.GetType().FullName} not implemented!"); }
                )?.Accept(TriviaConvertingVisitor) ?? SyntaxFactory.ParseTypeName("var");


                AccessorListSyntax accessors = null;
                if (!hasBody) {
                    var accessorList = new List<AccessorDeclarationSyntax>
                    {
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                    };
                    if (!isReadonly) {
                        accessorList.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                    }
                    accessors = SyntaxFactory.AccessorList(SyntaxFactory.List(accessorList));
                } else {
                    accessors = SyntaxFactory.AccessorList(
                        SyntaxFactory.List(
                            ((VBSyntax.PropertyBlockSyntax)node.Parent).Accessors.Select(a => (AccessorDeclarationSyntax)a.Accept(TriviaConvertingVisitor))
                        )
                    );
                }

                if (isIndexer)
                    return SyntaxFactory.IndexerDeclaration(
                        SyntaxFactory.List(attributes),
                        modifiers,
                        rawType,
                        null,
                        SyntaxFactory.BracketedParameterList(SyntaxFactory.SeparatedList(node.ParameterList.Parameters.Select(p => (ParameterSyntax)p.Accept(TriviaConvertingVisitor)))),
                        accessors
                    );
                else {
                    return SyntaxFactory.PropertyDeclaration(
                        SyntaxFactory.List(attributes),
                        modifiers,
                        rawType,
                        null,
                        ConvertIdentifier(node.Identifier, semanticModel), accessors,
                        null,
                        initializer,
                        SyntaxFactory.Token(initializer == null ? SyntaxKind.None : SyntaxKind.SemicolonToken));
                }
            }

            public override CSharpSyntaxNode VisitPropertyBlock(VBSyntax.PropertyBlockSyntax node)
            {
                return node.PropertyStatement.Accept(TriviaConvertingVisitor);
            }

            public override CSharpSyntaxNode VisitAccessorBlock(VBSyntax.AccessorBlockSyntax node)
            {
                SyntaxKind blockKind;
                bool isIterator = node.GetModifiers().Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.IteratorKeyword));
                var body = VisitStatements(node.Statements, isIterator);
                var attributes = ConvertAttributes(node.AccessorStatement.AttributeLists);
                var modifiers = ConvertModifiers(node.AccessorStatement.Modifiers, TokenContext.Local);

                switch (node.Kind()) {
                    case VBasic.SyntaxKind.GetAccessorBlock:
                        blockKind = SyntaxKind.GetAccessorDeclaration;
                        break;
                    case VBasic.SyntaxKind.SetAccessorBlock:
                        blockKind = SyntaxKind.SetAccessorDeclaration;
                        break;
                    case VBasic.SyntaxKind.AddHandlerAccessorBlock:
                        blockKind = SyntaxKind.AddAccessorDeclaration;
                        break;
                    case VBasic.SyntaxKind.RemoveHandlerAccessorBlock:
                        blockKind = SyntaxKind.RemoveAccessorDeclaration;
                        break;
                    default:
                        throw new NotSupportedException();
                }
                return SyntaxFactory.AccessorDeclaration(blockKind, attributes, modifiers, body);
            }

            public override CSharpSyntaxNode VisitMethodBlock(VBSyntax.MethodBlockSyntax node)
            {
                BaseMethodDeclarationSyntax block = (BaseMethodDeclarationSyntax)node.SubOrFunctionStatement.Accept(TriviaConvertingVisitor);
                bool isIterator = node.SubOrFunctionStatement.Modifiers.Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.IteratorKeyword));

                return block.WithBody(VisitStatements(node.Statements, isIterator));
            }

            private BlockSyntax VisitStatements(SyntaxList<VBSyntax.StatementSyntax> statements, bool isIterator)
            {
                return SyntaxFactory.Block(statements.SelectMany(s => s.Accept(CreateMethodBodyVisitor(isIterator))));
            }

            public override CSharpSyntaxNode VisitMethodStatement(VBSyntax.MethodStatementSyntax node)
            {
                var attributes = node.AttributeLists.SelectMany(ConvertAttribute);
                bool hasBody = node.Parent is VBSyntax.MethodBlockBaseSyntax;

                if ("Finalize".Equals(node.Identifier.ValueText, StringComparison.OrdinalIgnoreCase)
                    && node.Modifiers.Any(m => VBasic.VisualBasicExtensions.Kind(m) == VBasic.SyntaxKind.OverridesKeyword)) {
                    var decl = SyntaxFactory.DestructorDeclaration(
                        ConvertIdentifier(node.GetAncestor<VBSyntax.TypeBlockSyntax>().BlockStatement.Identifier, semanticModel)
                    ).WithAttributeLists(SyntaxFactory.List(attributes));
                    if (hasBody) return decl;
                    return decl.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                } else {
                    var tokenContext = GetMethodOrPropertyContext(node);
                    var modifiers = ConvertModifiers(node.Modifiers, tokenContext);

                    TypeParameterListSyntax typeParameters;
                    SyntaxList<TypeParameterConstraintClauseSyntax> constraints;
                    SplitTypeParameters(node.TypeParameterList, out typeParameters, out constraints);

                    var decl = SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.List(attributes),
                        modifiers,
                        (TypeSyntax)node.AsClause?.Type.Accept(TriviaConvertingVisitor) ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                        null,
                        ConvertIdentifier(node.Identifier, semanticModel),
                        typeParameters,
                        (ParameterListSyntax)node.ParameterList.Accept(TriviaConvertingVisitor),
                        constraints,
                        null,
                        null
                    );
                    if (hasBody) return decl;
                    return decl.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }
            }

            private TokenContext GetMethodOrPropertyContext(VBSyntax.StatementSyntax node)
            {
                var parentType = semanticModel.GetDeclaredSymbol(node).ContainingType;
                switch (parentType.TypeKind) {
                    case TypeKind.Module:
                        return TokenContext.MemberInModule;
                    case TypeKind.Class:
                        return TokenContext.MemberInClass;
                    case TypeKind.Interface:
                        return TokenContext.MemberInInterface;
                    case TypeKind.Struct:
                        return TokenContext.MemberInStruct;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(node));
                }
            }

            public override CSharpSyntaxNode VisitEventBlock(VBSyntax.EventBlockSyntax node)
            {
                var block = node.EventStatement;
                var attributes = block.AttributeLists.SelectMany(ConvertAttribute);
                var modifiers = ConvertModifiers(block.Modifiers, TokenContext.Member);

                var rawType = (TypeSyntax)block.AsClause?.Type.Accept(TriviaConvertingVisitor) ?? SyntaxFactory.ParseTypeName("var");

                return SyntaxFactory.EventDeclaration(
                    SyntaxFactory.List(attributes),
                    modifiers,
                    rawType,
                    null,
                    ConvertIdentifier(block.Identifier, semanticModel),
                    SyntaxFactory.AccessorList(SyntaxFactory.List(node.Accessors.Select(a => (AccessorDeclarationSyntax)a.Accept(TriviaConvertingVisitor))))
                );
            }

            public override CSharpSyntaxNode VisitEventStatement(VBSyntax.EventStatementSyntax node)
            {
                var attributes = node.AttributeLists.SelectMany(ConvertAttribute);
                var modifiers = ConvertModifiers(node.Modifiers, TokenContext.Member);
                var id = ConvertIdentifier(node.Identifier, semanticModel);

                if (node.AsClause == null) {
                    var delegateName = SyntaxFactory.Identifier(id.ValueText + "EventHandler");

                    var delegateDecl = SyntaxFactory.DelegateDeclaration(
                        SyntaxFactory.List<AttributeListSyntax>(),
                        modifiers,
                        SyntaxFactory.ParseTypeName("void"),
                        delegateName,
                        null,
                        (ParameterListSyntax)node.ParameterList.Accept(TriviaConvertingVisitor),
                        SyntaxFactory.List<TypeParameterConstraintClauseSyntax>()
                    );

                    var eventDecl = SyntaxFactory.EventFieldDeclaration(
                        SyntaxFactory.List(attributes),
                        modifiers,
                        SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName(delegateName),
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(id)))
                    );

                    additionalDeclarations.Add(node, new MemberDeclarationSyntax[] { delegateDecl });
                    return eventDecl;
                } else {
                    return SyntaxFactory.EventFieldDeclaration(
                        SyntaxFactory.List(attributes),
                        modifiers,
                        SyntaxFactory.VariableDeclaration((TypeSyntax)node.AsClause.Type.Accept(TriviaConvertingVisitor),
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(id)))
                    );
                }
                throw new NotSupportedException();
            }

            public override CSharpSyntaxNode VisitOperatorBlock(VBSyntax.OperatorBlockSyntax node)
            {
                var block = node.OperatorStatement;
                var attributes = block.AttributeLists.SelectMany(ConvertAttribute);
                var modifiers = ConvertModifiers(block.Modifiers, TokenContext.Member);
                return SyntaxFactory.OperatorDeclaration(
                    SyntaxFactory.List(attributes),
                    modifiers,
                    (TypeSyntax)block.AsClause?.Type.Accept(TriviaConvertingVisitor) ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    ConvertToken(block.OperatorToken),
                    (ParameterListSyntax)block.ParameterList.Accept(TriviaConvertingVisitor),
                    SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CreateMethodBodyVisitor()))),
                    null
                );
            }

            private VBasic.VisualBasicSyntaxVisitor<SyntaxList<StatementSyntax>> CreateMethodBodyVisitor(bool isIterator = false)
            {
                var methodBodyVisitor = new MethodBodyVisitor(semanticModel, TriviaConvertingVisitor, withBlockTempVariableNames, TriviaConvertingVisitor.TriviaConverter) {IsIterator = isIterator};
                return methodBodyVisitor.CommentConvertingVisitor;
            }

            public override CSharpSyntaxNode VisitConstructorBlock(VBSyntax.ConstructorBlockSyntax node)
            {
                var block = node.BlockStatement;
                var attributes = block.AttributeLists.SelectMany(ConvertAttribute);
                var modifiers = ConvertModifiers(block.Modifiers, TokenContext.Member);


                var ctor = (node.Statements.FirstOrDefault() as VBSyntax.ExpressionStatementSyntax)?.Expression as VBSyntax.InvocationExpressionSyntax;
                var ctorExpression = ctor?.Expression as VBSyntax.MemberAccessExpressionSyntax;
                var ctorArgs = (ArgumentListSyntax)ctor?.ArgumentList.Accept(TriviaConvertingVisitor);

                IEnumerable<VBSyntax.StatementSyntax> statements;
                ConstructorInitializerSyntax ctorCall;
                if (ctorExpression == null || !ctorExpression.Name.Identifier.IsKindOrHasMatchingText(VBasic.SyntaxKind.NewKeyword)) {
                    statements = node.Statements;
                    ctorCall = null;
                } else if (ctorExpression.Expression is VBSyntax.MyBaseExpressionSyntax) {
                    statements = node.Statements.Skip(1);
                    ctorCall = SyntaxFactory.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer, ctorArgs ?? SyntaxFactory.ArgumentList());
                } else if (ctorExpression.Expression is VBSyntax.MeExpressionSyntax || ctorExpression.Expression is VBSyntax.MyClassExpressionSyntax) {
                    statements = node.Statements.Skip(1);
                    ctorCall = SyntaxFactory.ConstructorInitializer(SyntaxKind.ThisConstructorInitializer, ctorArgs ?? SyntaxFactory.ArgumentList());
                } else {
                    statements = node.Statements;
                    ctorCall = null;
                }

                return SyntaxFactory.ConstructorDeclaration(
                    SyntaxFactory.List(attributes),
                    modifiers,
                    ConvertIdentifier(node.GetAncestor<VBSyntax.TypeBlockSyntax>().BlockStatement.Identifier, semanticModel),
                    (ParameterListSyntax)block.ParameterList.Accept(TriviaConvertingVisitor),
                    ctorCall,
                    SyntaxFactory.Block(statements.SelectMany(s => s.Accept(CreateMethodBodyVisitor())))
                );
            }

            public override CSharpSyntaxNode VisitTypeParameterList(VBSyntax.TypeParameterListSyntax node)
            {
                return SyntaxFactory.TypeParameterList(
                    SyntaxFactory.SeparatedList(node.Parameters.Select(p => (TypeParameterSyntax)p.Accept(TriviaConvertingVisitor)))
                );
            }

            public override CSharpSyntaxNode VisitParameterList(VBSyntax.ParameterListSyntax node)
            {
                if (node.Parent is VBSyntax.PropertyStatementSyntax) {
                    return SyntaxFactory.BracketedParameterList(SyntaxFactory.SeparatedList(node.Parameters.Select(p => (ParameterSyntax)p.Accept(TriviaConvertingVisitor))));
                }
                return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(node.Parameters.Select(p => (ParameterSyntax)p.Accept(TriviaConvertingVisitor))));
            }

            public override CSharpSyntaxNode VisitParameter(VBSyntax.ParameterSyntax node)
            {
                var id = ConvertIdentifier(node.Identifier.Identifier, semanticModel);
                var returnType = (TypeSyntax)node.AsClause?.Type.Accept(TriviaConvertingVisitor);
                if (node?.Parent?.Parent?.IsKind(VBasic.SyntaxKind.FunctionStatement,
                    VBasic.SyntaxKind.SubStatement) == true) {
                    returnType = returnType ?? SyntaxFactory.ParseTypeName("object");
                }

                var rankSpecifiers = ConvertArrayRankSpecifierSyntaxes(node.Identifier.ArrayRankSpecifiers);
                if (rankSpecifiers.Any()) {
                    returnType = SyntaxFactory.ArrayType(returnType, rankSpecifiers);
                }
                
                if (returnType != null && !SyntaxTokenExtensions.IsKind(node.Identifier.Nullable, SyntaxKind.None)) {
                    var arrayType = returnType as ArrayTypeSyntax;
                    if (arrayType == null) {
                        returnType = SyntaxFactory.NullableType(returnType);
                    } else {
                        returnType = arrayType.WithElementType(SyntaxFactory.NullableType(arrayType.ElementType));
                    }
                }
                EqualsValueClauseSyntax @default = null;
                if (node.Default != null) {
                    @default = SyntaxFactory.EqualsValueClause((ExpressionSyntax)node.Default?.Value.Accept(TriviaConvertingVisitor));
                }
                var attributes = node.AttributeLists.SelectMany(ConvertAttribute).ToList();
                int outAttributeIndex = attributes.FindIndex(a => a.Attributes.Single().Name.ToString() == "Out");
                var modifiers = ConvertModifiers(node.Modifiers, TokenContext.Local);
                if (outAttributeIndex > -1) {
                    attributes.RemoveAt(outAttributeIndex);
                    modifiers = modifiers.Replace(SyntaxFactory.Token(SyntaxKind.RefKeyword), SyntaxFactory.Token(SyntaxKind.OutKeyword));
                }
                return SyntaxFactory.Parameter(
                    SyntaxFactory.List(attributes),
                    modifiers,
                    returnType,
                    id,
                    @default
                );
            }

            #endregion

            #region Expressions

            public override CSharpSyntaxNode VisitAwaitExpression(VBSyntax.AwaitExpressionSyntax node)
            {
                return SyntaxFactory.AwaitExpression((ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitCatchBlock(VBSyntax.CatchBlockSyntax node)
            {
                var stmt = node.CatchStatement;
                CatchDeclarationSyntax catcher;
                if (stmt.IdentifierName == null)
                    catcher = null;
                else {
                    var typeInfo = semanticModel.GetTypeInfo(stmt.IdentifierName).Type;
                    catcher = SyntaxFactory.CatchDeclaration(
                        SyntaxFactory.ParseTypeName(typeInfo.ToMinimalDisplayString(semanticModel, node.SpanStart)),
                        ConvertIdentifier(stmt.IdentifierName.Identifier, semanticModel)
                    );
                }

                var filter = (CatchFilterClauseSyntax)stmt.WhenClause?.Accept(TriviaConvertingVisitor);

                return SyntaxFactory.CatchClause(
                    catcher,
                    filter,
                    SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CreateMethodBodyVisitor())))
                );
            }

            public override CSharpSyntaxNode VisitCatchFilterClause(VBSyntax.CatchFilterClauseSyntax node)
            {
                return SyntaxFactory.CatchFilterClause((ExpressionSyntax)node.Filter.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitFinallyBlock(VBSyntax.FinallyBlockSyntax node)
            {
                return SyntaxFactory.FinallyClause(SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CreateMethodBodyVisitor()))));
            }


            public override CSharpSyntaxNode VisitCTypeExpression(VBSyntax.CTypeExpressionSyntax node)
            {
                var expressionSyntax = (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor);
                var convertMethodForKeywordOrNull = GetConvertMethodForKeywordOrNull(node.Type);

                return convertMethodForKeywordOrNull != null ?
                    SyntaxFactory.InvocationExpression(convertMethodForKeywordOrNull,
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(expressionSyntax)))
                    ) // Hopefully will be a compile error if it's wrong
                    : (ExpressionSyntax)SyntaxFactory.CastExpression((TypeSyntax)node.Type.Accept(TriviaConvertingVisitor), expressionSyntax);
            }

            public override CSharpSyntaxNode VisitPredefinedCastExpression(VBSyntax.PredefinedCastExpressionSyntax node)
            {
                var expressionSyntax = (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor);
                if (SyntaxTokenExtensions.IsKind(node.Keyword, VBasic.SyntaxKind.CDateKeyword)) {
                    return SyntaxFactory.CastExpression(
                        SyntaxFactory.ParseTypeName("DateTime"),
                        expressionSyntax
                    );
                }

                var convertMethodForKeywordOrNull = GetConvertMethodForKeywordOrNull(node);

                return convertMethodForKeywordOrNull != null ? (ExpressionSyntax)
                    SyntaxFactory.InvocationExpression(convertMethodForKeywordOrNull,
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(expressionSyntax)))
                    ) // Hopefully will be a compile error if it's wrong
                    : SyntaxFactory.CastExpression(
                    SyntaxFactory.PredefinedType(ConvertToken(node.Keyword)),
                    (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor)
                );
            }

            private ExpressionSyntax GetConvertMethodForKeywordOrNull(SyntaxNode type)
            {
                var convertedType = semanticModel.GetTypeInfo(type).ConvertedType;
                return createConvertMethodsLookupByReturnType.TryGetValue(convertedType, out var convertMethodName)
                    ? SyntaxFactory.ParseExpression(convertMethodName) : null;
            }

            public override CSharpSyntaxNode VisitTryCastExpression(VBSyntax.TryCastExpressionSyntax node)
            {
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.AsExpression,
                    (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor),
                    (TypeSyntax)node.Type.Accept(TriviaConvertingVisitor)
                );
            }

            public override CSharpSyntaxNode VisitLiteralExpression(VBSyntax.LiteralExpressionSyntax node)
            {
                if (node.Token.Value == null) {
                    var type = semanticModel.GetTypeInfo(node).ConvertedType;
                    if (type == null) {
                        return Literal(null)
                            .WithTrailingTrivia(
                                SyntaxFactory.Comment("/* TODO Change to default(_) if this is not a reference type */"));
                    }
                    return !type.IsReferenceType ? SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(type.ToMinimalDisplayString(semanticModel, node.SpanStart))) : Literal(null);
                }
                return Literal(node.Token.Value);
            }

            public override CSharpSyntaxNode VisitInterpolatedStringExpression(VBSyntax.InterpolatedStringExpressionSyntax node)
            {
                return SyntaxFactory.InterpolatedStringExpression(SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken), SyntaxFactory.List(node.Contents.Select(c => (InterpolatedStringContentSyntax)c.Accept(TriviaConvertingVisitor))));
            }

            public override CSharpSyntaxNode VisitInterpolatedStringText(VBSyntax.InterpolatedStringTextSyntax node)
            {
                return SyntaxFactory.InterpolatedStringText(SyntaxFactory.Token(default(SyntaxTriviaList), SyntaxKind.InterpolatedStringTextToken, node.TextToken.Text, node.TextToken.ValueText, default(SyntaxTriviaList)));
            }

            public override CSharpSyntaxNode VisitInterpolation(VBSyntax.InterpolationSyntax node)
            {
                return SyntaxFactory.Interpolation((ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitInterpolationFormatClause(VBSyntax.InterpolationFormatClauseSyntax node)
            {
                return base.VisitInterpolationFormatClause(node);
            }

            public override CSharpSyntaxNode VisitMeExpression(VBSyntax.MeExpressionSyntax node)
            {
                return SyntaxFactory.ThisExpression();
            }

            public override CSharpSyntaxNode VisitMyBaseExpression(VBSyntax.MyBaseExpressionSyntax node)
            {
                return SyntaxFactory.BaseExpression();
            }

            public override CSharpSyntaxNode VisitParenthesizedExpression(VBSyntax.ParenthesizedExpressionSyntax node)
            {
                return SyntaxFactory.ParenthesizedExpression((ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitMemberAccessExpression(VBSyntax.MemberAccessExpressionSyntax node)
            {
                var simpleNameSyntax = (SimpleNameSyntax)node.Name.Accept(TriviaConvertingVisitor);

                var left = (ExpressionSyntax)node.Expression?.Accept(TriviaConvertingVisitor);
                if (left == null) {
                    if (!node.Parent.Parent.IsKind(VBasic.SyntaxKind.WithBlock)) {
                        return SyntaxFactory.MemberBindingExpression(simpleNameSyntax);
                    }

                    left = SyntaxFactory.IdentifierName(withBlockTempVariableNames.Peek());
                }

                if (node.Expression.IsKind(VBasic.SyntaxKind.GlobalName)) {
                    return SyntaxFactory.AliasQualifiedName((IdentifierNameSyntax)left, simpleNameSyntax);
                } else {
                    var memberAccessExpressionSyntax = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, QualifyNode(node.Expression, left), simpleNameSyntax);
                    if (semanticModel.GetSymbolInfo(node).Symbol is IMethodSymbol methodSymbol && methodSymbol.ReturnType.Equals(semanticModel.GetTypeInfo(node).ConvertedType)) {
                        var visitMemberAccessExpression = SyntaxFactory.InvocationExpression(memberAccessExpressionSyntax, SyntaxFactory.ArgumentList());
                        return visitMemberAccessExpression;
                    } else {
                        return memberAccessExpressionSyntax;
                    }
                }
            }

            public override CSharpSyntaxNode VisitConditionalAccessExpression(VBSyntax.ConditionalAccessExpressionSyntax node)
            {
                return SyntaxFactory.ConditionalAccessExpression((ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor), (ExpressionSyntax)node.WhenNotNull.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitArgumentList(VBSyntax.ArgumentListSyntax node)
            {
                if (node.Parent.IsKind(VBasic.SyntaxKind.Attribute)) {
                    return SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(node.Arguments.Select(ToAttributeArgument)));
                }
                return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(node.Arguments.Select(a => (ArgumentSyntax)a.Accept(TriviaConvertingVisitor))));
            }

            public override CSharpSyntaxNode VisitSimpleArgument(VBSyntax.SimpleArgumentSyntax node)
            {
                int argID = ((VBSyntax.ArgumentListSyntax)node.Parent).Arguments.IndexOf(node);
                var invocation = node.Parent.Parent;
                if (invocation is VBSyntax.ArrayCreationExpressionSyntax)
                    return node.Expression.Accept(TriviaConvertingVisitor);
                var symbol = invocation.TypeSwitch(
                    (VBSyntax.InvocationExpressionSyntax e) => ExtractMatch(semanticModel.GetSymbolInfo(e)),
                    (VBSyntax.ObjectCreationExpressionSyntax e) => ExtractMatch(semanticModel.GetSymbolInfo(e)),
                    (VBSyntax.RaiseEventStatementSyntax e) => ExtractMatch(semanticModel.GetSymbolInfo(e.Name)),
                    _ => { throw new NotSupportedException(); }
                );
                SyntaxToken token = default(SyntaxToken);
                if (symbol != null) {
                    var parameterKinds = symbol.GetParameters().Select(param => param.RefKind).ToList();
                    //WARNING: If named parameters can reach here it won't work properly for them
                    var refKind = argID >= parameterKinds.Count && symbol.IsParams() ? RefKind.None : parameterKinds[argID];
                    switch (refKind) {
                        case RefKind.None:
                            token = default(SyntaxToken);
                            break;
                        case RefKind.Ref:
                            token = SyntaxFactory.Token(SyntaxKind.RefKeyword);
                            break;
                        case RefKind.Out:
                            token = SyntaxFactory.Token(SyntaxKind.OutKeyword);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                return SyntaxFactory.Argument(
                    node.IsNamed ? SyntaxFactory.NameColon((IdentifierNameSyntax)node.NameColonEquals.Name.Accept(TriviaConvertingVisitor)) : null,
                    token,
                    (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor)
                );
            }

            private ISymbol ExtractMatch(SymbolInfo info)
            {
                if (info.Symbol == null && info.CandidateSymbols.Length == 0)
                    return null;
                if (info.Symbol != null)
                    return info.Symbol;
                if (info.CandidateSymbols.Length == 1)
                    return info.CandidateSymbols[0];
                return null;
            }

            private AttributeArgumentSyntax ToAttributeArgument(VBSyntax.ArgumentSyntax arg)
            {
                if (!(arg is VBSyntax.SimpleArgumentSyntax))
                    throw new NotSupportedException();
                var a = (VBSyntax.SimpleArgumentSyntax)arg;
                var attr = SyntaxFactory.AttributeArgument((ExpressionSyntax)a.Expression.Accept(TriviaConvertingVisitor));
                if (a.IsNamed) {
                    attr = attr.WithNameEquals(SyntaxFactory.NameEquals((IdentifierNameSyntax)a.NameColonEquals.Name.Accept(TriviaConvertingVisitor)));
                }
                return attr;
            }

            public override CSharpSyntaxNode VisitNameOfExpression(VBSyntax.NameOfExpressionSyntax node)
            {
                return SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("nameof"), SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument((ExpressionSyntax)node.Argument.Accept(TriviaConvertingVisitor)))));
            }

            public override CSharpSyntaxNode VisitEqualsValue(VBSyntax.EqualsValueSyntax node)
            {
                return SyntaxFactory.EqualsValueClause((ExpressionSyntax)node.Value.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitObjectMemberInitializer(VBSyntax.ObjectMemberInitializerSyntax node)
            {
                var memberDeclaratorSyntaxs = SyntaxFactory.SeparatedList(
                    node.Initializers.Select(initializer => initializer.Accept(TriviaConvertingVisitor)).Cast<ExpressionSyntax>());
                return SyntaxFactory.InitializerExpression(SyntaxKind.ObjectInitializerExpression, memberDeclaratorSyntaxs);
            }

            public override CSharpSyntaxNode VisitAnonymousObjectCreationExpression(VBSyntax.AnonymousObjectCreationExpressionSyntax node)
            {
                var memberDeclaratorSyntaxs = SyntaxFactory.SeparatedList(
                    node.Initializer.Initializers.Select(initializer => initializer.Accept(TriviaConvertingVisitor)).Cast<AnonymousObjectMemberDeclaratorSyntax>());
                return SyntaxFactory.AnonymousObjectCreationExpression(memberDeclaratorSyntaxs);
            }

            public override CSharpSyntaxNode VisitObjectCreationExpression(VBSyntax.ObjectCreationExpressionSyntax node)
            {
                return SyntaxFactory.ObjectCreationExpression(
                    (TypeSyntax)node.Type.Accept(TriviaConvertingVisitor),
                    // VB can omit empty arg lists:
                    (ArgumentListSyntax)node.ArgumentList?.Accept(TriviaConvertingVisitor) ?? SyntaxFactory.ArgumentList(),
                    (InitializerExpressionSyntax)node.Initializer?.Accept(TriviaConvertingVisitor)
                );
            }

            public override CSharpSyntaxNode VisitArrayCreationExpression(VBSyntax.ArrayCreationExpressionSyntax node)
            {
                IEnumerable<ExpressionSyntax> arguments;
                if (node.ArrayBounds != null) {
                    arguments = ConvertArrayBounds(node.ArrayBounds);
                } else
                    arguments = Enumerable.Empty<ExpressionSyntax>();
                var bounds = ConvertArrayRankSpecifierSyntaxes(node.RankSpecifiers);
                return SyntaxFactory.ArrayCreationExpression(
                    SyntaxFactory.ArrayType((TypeSyntax)node.Type.Accept(TriviaConvertingVisitor), bounds),
                    (InitializerExpressionSyntax)node.Initializer?.Accept(TriviaConvertingVisitor)
                );
            }

            private SyntaxList<ArrayRankSpecifierSyntax> ConvertArrayRankSpecifierSyntaxes(SyntaxList<VBSyntax.ArrayRankSpecifierSyntax> arrayRankSpecifierSyntaxs)
            {
                return SyntaxFactory.List(arrayRankSpecifierSyntaxs.Select(r => (ArrayRankSpecifierSyntax)r.Accept(TriviaConvertingVisitor)));
            }

            private IEnumerable<ExpressionSyntax> ConvertArrayBounds(VBSyntax.ArgumentListSyntax argumentListSyntax)
            {
                return argumentListSyntax.Arguments.Select(a => IncreaseArrayUpperBoundExpression(((VBSyntax.SimpleArgumentSyntax)a).Expression));
            }

            public override CSharpSyntaxNode VisitCollectionInitializer(VBSyntax.CollectionInitializerSyntax node)
            {
                if (node.Initializers.Count == 0 && node.Parent is VBSyntax.ArrayCreationExpressionSyntax)
                    return null;
                var initializer = SyntaxFactory.InitializerExpression(SyntaxKind.CollectionInitializerExpression, SyntaxFactory.SeparatedList(node.Initializers.Select(i => (ExpressionSyntax)i.Accept(TriviaConvertingVisitor))));
                var typeInfo = semanticModel.GetTypeInfo(node);
                return typeInfo.Type == null && (typeInfo.ConvertedType?.SpecialType == SpecialType.System_Collections_IEnumerable || typeInfo.ConvertedType?.IsKind(SymbolKind.ArrayType) == true)
                    ? (CSharpSyntaxNode)SyntaxFactory.ImplicitArrayCreationExpression(initializer)
                    : initializer;
            }

            public override CSharpSyntaxNode VisitNamedFieldInitializer(VBSyntax.NamedFieldInitializerSyntax node)
            {
                if (node?.Parent?.Parent is VBSyntax.AnonymousObjectCreationExpressionSyntax) {
                    return SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName(ConvertIdentifier(node.Name.Identifier, semanticModel))),
                        (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor));
                }

                return SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                    (ExpressionSyntax)node.Name.Accept(TriviaConvertingVisitor),
                    (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor)
                );
            }

            public override CSharpSyntaxNode VisitObjectCollectionInitializer(VBSyntax.ObjectCollectionInitializerSyntax node)
            {
                return node.Initializer.Accept(TriviaConvertingVisitor); //Dictionary initializer comes through here despite the FROM keyword not being in the source code
            }

            ExpressionSyntax IncreaseArrayUpperBoundExpression(VBSyntax.ExpressionSyntax expr)
            {
                var constant = semanticModel.GetConstantValue(expr);
                if (constant.HasValue && constant.Value is int)
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal((int)constant.Value + 1));

                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.SubtractExpression,
                    (ExpressionSyntax)expr.Accept(TriviaConvertingVisitor), SyntaxFactory.Token(SyntaxKind.PlusToken), SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));
            }

            public override CSharpSyntaxNode VisitBinaryConditionalExpression(VBSyntax.BinaryConditionalExpressionSyntax node)
            {
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.CoalesceExpression,
                    (ExpressionSyntax)node.FirstExpression.Accept(TriviaConvertingVisitor),
                    (ExpressionSyntax)node.SecondExpression.Accept(TriviaConvertingVisitor)
                );
            }

            public override CSharpSyntaxNode VisitTernaryConditionalExpression(VBSyntax.TernaryConditionalExpressionSyntax node)
            {
                return SyntaxFactory.ConditionalExpression(
                    (ExpressionSyntax)node.Condition.Accept(TriviaConvertingVisitor),
                    (ExpressionSyntax)node.WhenTrue.Accept(TriviaConvertingVisitor),
                    (ExpressionSyntax)node.WhenFalse.Accept(TriviaConvertingVisitor)
                );
            }

            public override CSharpSyntaxNode VisitTypeOfExpression(VBSyntax.TypeOfExpressionSyntax node)
            {
                var expr = SyntaxFactory.BinaryExpression(
                    SyntaxKind.IsExpression,
                    (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor),
                    (TypeSyntax)node.Type.Accept(TriviaConvertingVisitor)
                );
                if (node.IsKind(VBasic.SyntaxKind.TypeOfIsNotExpression))
                    return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, expr);
                else
                    return expr;
            }

            public override CSharpSyntaxNode VisitUnaryExpression(VBSyntax.UnaryExpressionSyntax node)
            {
                var expr = (ExpressionSyntax)node.Operand.Accept(TriviaConvertingVisitor);
                if (node.IsKind(VBasic.SyntaxKind.AddressOfExpression))
                    return expr;
                var kind = ConvertToken(VBasic.VisualBasicExtensions.Kind(node), TokenContext.Local);
                return SyntaxFactory.PrefixUnaryExpression(
                    kind,
                    SyntaxFactory.Token(CSharpUtil.GetExpressionOperatorTokenKind(kind)),
                    expr
                );
            }

            public override CSharpSyntaxNode VisitBinaryExpression(VBSyntax.BinaryExpressionSyntax node)
            {
                if (node.IsKind(VBasic.SyntaxKind.IsExpression)) {
                    ExpressionSyntax otherArgument = null;
                    if (node.Left.IsKind(VBasic.SyntaxKind.NothingLiteralExpression)) {
                        otherArgument = (ExpressionSyntax)node.Right.Accept(TriviaConvertingVisitor);
                    }
                    if (node.Right.IsKind(VBasic.SyntaxKind.NothingLiteralExpression)) {
                        otherArgument = (ExpressionSyntax)node.Left.Accept(TriviaConvertingVisitor);
                    }
                    if (otherArgument != null) {
                        return SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, otherArgument, SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    }
                }
                if (node.IsKind(VBasic.SyntaxKind.IsNotExpression)) {
                    ExpressionSyntax otherArgument = null;
                    if (node.Left.IsKind(VBasic.SyntaxKind.NothingLiteralExpression)) {
                        otherArgument = (ExpressionSyntax)node.Right.Accept(TriviaConvertingVisitor);
                    }
                    if (node.Right.IsKind(VBasic.SyntaxKind.NothingLiteralExpression)) {
                        otherArgument = (ExpressionSyntax)node.Left.Accept(TriviaConvertingVisitor);
                    }
                    if (otherArgument != null) {
                        return SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression, otherArgument, SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    }
                }
                var kind = ConvertToken(VBasic.VisualBasicExtensions.Kind(node), TokenContext.Local);
                return SyntaxFactory.BinaryExpression(
                    kind,
                    (ExpressionSyntax)node.Left.Accept(TriviaConvertingVisitor),
                    SyntaxFactory.Token(CSharpUtil.GetExpressionOperatorTokenKind(kind)),
                    (ExpressionSyntax)node.Right.Accept(TriviaConvertingVisitor)
                );
            }

            public override CSharpSyntaxNode VisitInvocationExpression(VBSyntax.InvocationExpressionSyntax node)
            {
                var invocationSymbol = ExtractMatch(semanticModel.GetSymbolInfo(node));
                var symbol = ExtractMatch(semanticModel.GetSymbolInfo(node.Expression));
                if (invocationSymbol?.IsIndexer() == true || symbol?.GetReturnType()?.IsArrayType() == true && !(symbol is IMethodSymbol)) //The null case happens quite a bit - should try to fix
                {
                    return SyntaxFactory.ElementAccessExpression(
                        (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor),
                        SyntaxFactory.BracketedArgumentList(SyntaxFactory.SeparatedList(node.ArgumentList.Arguments.Select(a => (ArgumentSyntax)a.Accept(TriviaConvertingVisitor)))));
                }

                return SyntaxFactory.InvocationExpression(
                    (ExpressionSyntax)node.Expression.Accept(TriviaConvertingVisitor),
                    (ArgumentListSyntax)node.ArgumentList.Accept(TriviaConvertingVisitor)
                );
            }

            public override CSharpSyntaxNode VisitSingleLineLambdaExpression(VBSyntax.SingleLineLambdaExpressionSyntax node)
            {
                CSharpSyntaxNode body;
                if (node.Body is VBSyntax.ExpressionSyntax)
                    body = node.Body.Accept(TriviaConvertingVisitor);
                else {
                    var stmt = node.Body.Accept(CreateMethodBodyVisitor());
                    if (stmt.Count == 1)
                        body = stmt[0];
                    else {
                        body = SyntaxFactory.Block(stmt);
                    }
                }
                var param = (ParameterListSyntax)node.SubOrFunctionHeader.ParameterList.Accept(TriviaConvertingVisitor);
                if (param.Parameters.Count == 1)
                    return SyntaxFactory.SimpleLambdaExpression(param.Parameters[0], body);
                return SyntaxFactory.ParenthesizedLambdaExpression(param, body);
            }

            public override CSharpSyntaxNode VisitMultiLineLambdaExpression(VBSyntax.MultiLineLambdaExpressionSyntax node)
            {
                var body = SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CreateMethodBodyVisitor())));
                var param = (ParameterListSyntax)node.SubOrFunctionHeader.ParameterList.Accept(TriviaConvertingVisitor);
                if (param.Parameters.Count == 1)
                    return SyntaxFactory.SimpleLambdaExpression(param.Parameters[0], body);
                return SyntaxFactory.ParenthesizedLambdaExpression(param, body);
            }

            #endregion

            #region Type Name / Modifier

            public override CSharpSyntaxNode VisitPredefinedType(VBSyntax.PredefinedTypeSyntax node)
            {
                if (SyntaxTokenExtensions.IsKind(node.Keyword, VBasic.SyntaxKind.DateKeyword)) {
                    return SyntaxFactory.IdentifierName("System.DateTime");
                }
                return SyntaxFactory.PredefinedType(ConvertToken(node.Keyword));
            }

            public override CSharpSyntaxNode VisitNullableType(VBSyntax.NullableTypeSyntax node)
            {
                return SyntaxFactory.NullableType((TypeSyntax)node.ElementType.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitArrayType(VBSyntax.ArrayTypeSyntax node)
            {
                return SyntaxFactory.ArrayType((TypeSyntax)node.ElementType.Accept(TriviaConvertingVisitor), SyntaxFactory.List(node.RankSpecifiers.Select(r => (ArrayRankSpecifierSyntax)r.Accept(TriviaConvertingVisitor))));
            }

            public override CSharpSyntaxNode VisitArrayRankSpecifier(VBSyntax.ArrayRankSpecifierSyntax node)
            {
                return SyntaxFactory.ArrayRankSpecifier(SyntaxFactory.SeparatedList(Enumerable.Repeat<ExpressionSyntax>(SyntaxFactory.OmittedArraySizeExpression(), node.Rank)));
            }

            private void SplitTypeParameters(VBSyntax.TypeParameterListSyntax typeParameterList, out TypeParameterListSyntax parameters, out SyntaxList<TypeParameterConstraintClauseSyntax> constraints)
            {
                parameters = null;
                constraints = SyntaxFactory.List<TypeParameterConstraintClauseSyntax>();
                if (typeParameterList == null)
                    return;
                var paramList = new List<TypeParameterSyntax>();
                var constraintList = new List<TypeParameterConstraintClauseSyntax>();
                foreach (var p in typeParameterList.Parameters) {
                    var tp = (TypeParameterSyntax)p.Accept(TriviaConvertingVisitor);
                    paramList.Add(tp);
                    var constraint = (TypeParameterConstraintClauseSyntax)p.TypeParameterConstraintClause?.Accept(TriviaConvertingVisitor);
                    if (constraint != null)
                        constraintList.Add(constraint);
                }
                parameters = SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(paramList));
                constraints = SyntaxFactory.List(constraintList);
            }

            public override CSharpSyntaxNode VisitTypeParameter(VBSyntax.TypeParameterSyntax node)
            {
                SyntaxToken variance = default(SyntaxToken);
                if (!SyntaxTokenExtensions.IsKind(node.VarianceKeyword, VBasic.SyntaxKind.None)) {
                    variance = SyntaxFactory.Token(SyntaxTokenExtensions.IsKind(node.VarianceKeyword, VBasic.SyntaxKind.InKeyword) ? SyntaxKind.InKeyword : SyntaxKind.OutKeyword);
                }
                return SyntaxFactory.TypeParameter(SyntaxFactory.List<AttributeListSyntax>(), variance, ConvertIdentifier(node.Identifier, semanticModel));
            }

            public override CSharpSyntaxNode VisitTypeParameterSingleConstraintClause(VBSyntax.TypeParameterSingleConstraintClauseSyntax node)
            {
                var id = SyntaxFactory.IdentifierName(ConvertIdentifier(((VBSyntax.TypeParameterSyntax)node.Parent).Identifier, semanticModel));
                return SyntaxFactory.TypeParameterConstraintClause(id, SyntaxFactory.SingletonSeparatedList((TypeParameterConstraintSyntax)node.Constraint.Accept(TriviaConvertingVisitor)));
            }

            public override CSharpSyntaxNode VisitTypeParameterMultipleConstraintClause(VBSyntax.TypeParameterMultipleConstraintClauseSyntax node)
            {
                var id = SyntaxFactory.IdentifierName(ConvertIdentifier(((VBSyntax.TypeParameterSyntax)node.Parent).Identifier, semanticModel));
                return SyntaxFactory.TypeParameterConstraintClause(id, SyntaxFactory.SeparatedList(node.Constraints.Select(c => (TypeParameterConstraintSyntax)c.Accept(TriviaConvertingVisitor))));
            }

            public override CSharpSyntaxNode VisitSpecialConstraint(VBSyntax.SpecialConstraintSyntax node)
            {
                if (SyntaxTokenExtensions.IsKind(node.ConstraintKeyword, VBasic.SyntaxKind.NewKeyword))
                    return SyntaxFactory.ConstructorConstraint();
                return SyntaxFactory.ClassOrStructConstraint(node.IsKind(VBasic.SyntaxKind.ClassConstraint) ? SyntaxKind.ClassConstraint : SyntaxKind.StructConstraint);
            }

            public override CSharpSyntaxNode VisitTypeConstraint(VBSyntax.TypeConstraintSyntax node)
            {
                return SyntaxFactory.TypeConstraint((TypeSyntax)node.Type.Accept(TriviaConvertingVisitor));
            }

            #endregion

            #region NameSyntax

            public override CSharpSyntaxNode VisitIdentifierName(VBSyntax.IdentifierNameSyntax node)
            {
                var identifier = SyntaxFactory.IdentifierName(ConvertIdentifier(node.Identifier, semanticModel, node.GetAncestor<VBSyntax.AttributeSyntax>() != null));

                return !node.Parent.IsKind(VBasic.SyntaxKind.SimpleMemberAccessExpression, VBasic.SyntaxKind.QualifiedName, VBasic.SyntaxKind.NameColonEquals, VBasic.SyntaxKind.ImportsStatement, VBasic.SyntaxKind.NamespaceStatement, VBasic.SyntaxKind.NamedFieldInitializer) 
                    ? QualifyNode(node, identifier) : identifier;
            }

            private ExpressionSyntax QualifyNode(SyntaxNode node, ExpressionSyntax defaultNode)
            {
                if (!(node is VBSyntax.NameSyntax)) return defaultNode;
                var referenceSymbolFormat = new SymbolDisplayFormat(SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining, SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces, SymbolDisplayGenericsOptions.IncludeTypeParameters, SymbolDisplayMemberOptions.IncludeContainingType);

                var targetSymbolInfo = GetSymbolInfoInDocument(node);

                var qualifiedName = targetSymbolInfo?.ToDisplayString(referenceSymbolFormat);
                var sourceText = node.WithoutTrivia().GetText().ToString().Trim();
                if (qualifiedName == null || sourceText.Length >= qualifiedName.Length ||
                    !qualifiedName.EndsWith(sourceText, StringComparison.Ordinal)) return defaultNode;

                var typeBlockSyntax = node.GetAncestor<VBSyntax.TypeBlockSyntax>();

                var typeOrNamespace = targetSymbolInfo.ContainingNamespace.ToDisplayString(referenceSymbolFormat);
                if (typeBlockSyntax != null) {
                    var declaredSymbol = semanticModel.GetDeclaredSymbol(typeBlockSyntax);
                    var prefixes = GetSymbolQualification(declaredSymbol)
                    .Where(x => x != null).Select(p => p.ToDisplayString(referenceSymbolFormat) + ".");
                    var firstMatch = prefixes.FirstOrDefault(p => qualifiedName.StartsWith(p));
                    if (firstMatch != null)
                    {
                        // CSharp allows partial qualification within the current type's parent namespace
                        qualifiedName = qualifiedName.Substring(firstMatch.Length);
                    }
                    else if (!targetSymbolInfo.IsNamespace() && importedNamespaces.ContainsKey(typeOrNamespace))
                    {
                        // An import matches the entire namespace, which means it's not a partially qualified thing that would need extra help in CSharp
                        qualifiedName = qualifiedName.Substring(typeOrNamespace.Length + 1);
                    }
                }
                return qualifiedName != defaultNode.ToString() ? 
                    SyntaxFactory.ParseName(qualifiedName.Replace(node.ToString(), defaultNode.ToString()))
                    : defaultNode;
            }

            private IEnumerable<ISymbol> GetSymbolQualification(ISymbol symbol)
            {
                return FollowProperty(symbol, s => s.ContainingSymbol);
            }

            private static IEnumerable<T> FollowProperty<T>(T start, Func<T, T> getProperty) where T : class
            {
                for (var current = start; current != null; current = getProperty(current))
                {
                    yield return current;
                }
            }

            /// <returns>The ISymbol if available in this document, otherwise null</returns>
            private ISymbol GetSymbolInfoInDocument(SyntaxNode node)
            {
                return semanticModel.SyntaxTree == node.SyntaxTree ? semanticModel.GetSymbolInfo(node).Symbol : null;
            }

            public override CSharpSyntaxNode VisitQualifiedName(VBSyntax.QualifiedNameSyntax node)
            {
                var lhsSyntax = (NameSyntax)node.Left.Accept(TriviaConvertingVisitor);
                var rhsSyntax = (SimpleNameSyntax)node.Right.Accept(TriviaConvertingVisitor);

                var qualifiedName = node.Parent.IsKind(VBasic.SyntaxKind.NamespaceStatement)
                    ? lhsSyntax
                    : QualifyNode(node.Left, lhsSyntax);
                return node.Left.IsKind(VBasic.SyntaxKind.GlobalName)
                    ? (CSharpSyntaxNode)SyntaxFactory.AliasQualifiedName((IdentifierNameSyntax)lhsSyntax, rhsSyntax)
                    : SyntaxFactory.QualifiedName((NameSyntax) qualifiedName, rhsSyntax);
            }

            public override CSharpSyntaxNode VisitGenericName(VBSyntax.GenericNameSyntax node)
            {
                return SyntaxFactory.GenericName(ConvertIdentifier(node.Identifier, semanticModel), (TypeArgumentListSyntax)node.TypeArgumentList?.Accept(TriviaConvertingVisitor));
            }

            public override CSharpSyntaxNode VisitTypeArgumentList(VBSyntax.TypeArgumentListSyntax node)
            {
                return SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(node.Arguments.Select(a => (TypeSyntax)a.Accept(TriviaConvertingVisitor))));
            }

            #endregion
        }
    }
}
