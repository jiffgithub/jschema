﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.
// Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.JSchema.Generator
{
    /// <summary>
    /// Generate the text of a class.
    /// </summary>
    public class ClassGenerator : ClassOrInterfaceGenerator
    {
        private readonly string _baseInterfaceName;
        private readonly bool _generateOverrides;
        private readonly bool _generateCloningCode;
        private readonly string _syntaxInterfaceName;
        private readonly string _kindEnumName;

        // Name used for the parameters of Equals methods and copy ctor.
        private const string OtherParameter = "other";

        private const string CountProperty = "Count";
        private const string EqualsMethod = "Equals";
        private const string GetHashCodeMethod = "GetHashCode";
        private const string ReferenceEqualsMethod = "ReferenceEquals";
        private const string IEquatableType = "IEquatable";
        private const string ObjectType = "Object";
        private const string IntTypeAlias = "int";

        private const string InitMethod = "Init";
        private const string DeepCloneMethod = "DeepClone";
        private const string DeepCloneCoreMethod = "DeepCloneCore";

        private const string TempVariableNameBase = "value_";
        private const string GetHashCodeResultVariableName = "result";

        private const int GetHashCodeSeedValue = 17;
        private const int GetHashCodeCombiningValue = 31;

        // Value used to construct unique names for each of the loop variables
        // used in the implementation of a method.
        private int _variableCount = 0;

        public ClassGenerator(
            JsonSchema rootSchema,
            string interfaceName,
            HintDictionary hintDictionary,
            bool generateOverrides,
            bool generateCloningCode,
            string syntaxInterfaceName,
            string kindEnumName)
            : base(rootSchema, hintDictionary)
        {
            _baseInterfaceName = interfaceName;
            _generateOverrides = generateOverrides;
            _generateCloningCode = generateCloningCode;
            _syntaxInterfaceName = syntaxInterfaceName;
            _kindEnumName = kindEnumName;
        }

        public override BaseTypeDeclarationSyntax CreateTypeDeclaration(JsonSchema schema)
        {
            var classDeclaration = SyntaxFactory.ClassDeclaration(TypeName)
                .WithModifiers(
                    SyntaxFactory.TokenList(
                        SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                        SyntaxFactory.Token(SyntaxKind.PartialKeyword)));

            var baseTypes = new List<BaseTypeSyntax>();

            // If this class implements an interface, add the interface to
            // the base type list.
            if (_baseInterfaceName != null)
            {
                SimpleBaseTypeSyntax interfaceType =
                    SyntaxFactory.SimpleBaseType(
                        SyntaxFactory.ParseTypeName(_baseInterfaceName));

                baseTypes.Add(interfaceType);
            }

            // If we were asked to generate cloning code, add the necessary interface.
            if (_generateCloningCode)
            {
                SimpleBaseTypeSyntax interfaceType =
                    SyntaxFactory.SimpleBaseType(
                        SyntaxFactory.ParseTypeName(_syntaxInterfaceName));

                baseTypes.Add(interfaceType);
            }

            var iEquatable = SyntaxFactory.SimpleBaseType(
                SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier(IEquatableType),
                    SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(
                        new TypeSyntax[] {
                        SyntaxFactory.ParseTypeName(TypeName)
                        }))));

            baseTypes.Add(iEquatable);

            AddUsing("System"); // For IEquatable<T>

            if (baseTypes.Count > 0)
            {
                SeparatedSyntaxList<BaseTypeSyntax> separatedBaseList = SyntaxFactory.SeparatedList(baseTypes);
                BaseListSyntax baseList = SyntaxFactory.BaseList(separatedBaseList);
                classDeclaration = classDeclaration.WithBaseList(baseList);
            }

            return classDeclaration;
        }

        public override void AddMembers(JsonSchema schema)
        {
            var members = new List<MemberDeclarationSyntax>();

            if (_generateCloningCode)
            {
              members.Add(GenerateSyntaxKindProperty());
            }
                
            members.AddRange(GenerateProperties(schema));

            if (_generateOverrides)
            {
                members.AddRange(new MemberDeclarationSyntax[]
                {
                    OverrideObjectEquals(),
                    OverrideGetHashCode(schema),
                    ImplementIEquatableEquals(schema)
                });
            }

            if (_generateCloningCode)
            {
                members.AddRange(new MemberDeclarationSyntax[]
                {
                    GenerateDefaultConstructor(),
                    GeneratePropertyCtor(),
                    GenerateCopyConstructor(),
                    GenerateISyntaxDeepClone(),
                    GenerateDeepClone(),
                    GenerateDeepCloneCore(),
                    GenerateInitMethod()
                });
            }

            SyntaxList<MemberDeclarationSyntax> memberList = SyntaxFactory.List(members);

            TypeDeclaration = (TypeDeclaration as ClassDeclarationSyntax).WithMembers(memberList);
        }

        private PropertyDeclarationSyntax GenerateSyntaxKindProperty()
        {
            return SyntaxFactory.PropertyDeclaration(
                default(SyntaxList<AttributeListSyntax>),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                SyntaxFactory.ParseTypeName(_kindEnumName),
                default(ExplicitInterfaceSpecifierSyntax),
                SyntaxFactory.Identifier("SyntaxKind"),
                SyntaxFactory.AccessorList(
                    SyntaxFactory.SingletonList(
                        SyntaxHelper.MakeGetAccessor(
                            SyntaxFactory.Block(
                                SyntaxFactory.ReturnStatement(
                                    SyntaxFactory.MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName(_kindEnumName),
                                        SyntaxFactory.IdentifierName(TypeName))))))))
                .WithLeadingTrivia(
                    SyntaxHelper.MakeDocComment(Resources.SyntaxInterfaceKindDescription));
        }

        private ConstructorDeclarationSyntax GenerateDefaultConstructor()
        {
            return SyntaxFactory.ConstructorDeclaration(
                default(SyntaxList<AttributeListSyntax>),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                SyntaxFactory.Identifier(TypeName),
                SyntaxFactory.ParameterList(),
                default(ConstructorInitializerSyntax),
                SyntaxFactory.Block())
                .WithLeadingTrivia(
                    SyntaxHelper.MakeDocComment(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.DefaultCtorSummary,
                            TypeName)));
        }

        private ConstructorDeclarationSyntax GeneratePropertyCtor()
        {
            // Generate the parameter list for the ctor. It takes the same parameters as
            // the Init method.
            SeparatedSyntaxList<ParameterSyntax> parameterList = GenerateInitMethodParameterList();

            // Generate the argument list that will be passed from the copy ctor to the
            // Init method.
            ExpressionSyntax[] arguments = GetPropertyNames()
                .Select(name =>  SyntaxFactory.IdentifierName(name.ToCamelCase()))
                .ToArray();

            return SyntaxFactory.ConstructorDeclaration(
                default(SyntaxList<AttributeListSyntax>),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                SyntaxFactory.Identifier(TypeName),
                SyntaxFactory.ParameterList(parameterList),
                default(ConstructorInitializerSyntax),
                SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.IdentifierName(InitMethod),
                            SyntaxHelper.ArgumentList(arguments)))))
                .WithLeadingTrivia(
                    SyntaxHelper.MakeDocComment(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.PropertyCtorSummary,
                            TypeName),
                        paramDescriptionDictionary: MakePropertyCtorParamDescriptions()));
        }

        /// <summary>
        /// Synthesize the type of the property as it should appear in the parameter list
        /// of the generated class's <code>Init</code> method.
        /// </summary>
        /// <remarks>
        /// For array-valued properties, the property type stored in the
        /// PropertyInfoDictionary is <code>IList&lt;T></code>. But in the parameter list
        /// of the <code>Init</code> method, the type appears as
        /// <code>IEnumerable&lt;T></code>.
        /// </remarks>
        private TypeSyntax GetParameterListType(string name)
        {
            TypeSyntax type = PropertyInfoDictionary[name.ToCamelCase()].Type;

            string typeName = type.ToString().Replace("IList<", "IEnumerable<");

            return SyntaxFactory.ParseTypeName(typeName);
        }

        private Dictionary<string, string> MakePropertyCtorParamDescriptions()
        {
            var result = new Dictionary<string, string>();

            foreach (string propertyName in GetPropertyNames())
            {
                string paramName = propertyName.ToCamelCase();

                result[paramName] = string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.PropertyCtorParamDescription,
                    propertyName);
            }

            return result;
        }

        private ConstructorDeclarationSyntax GenerateCopyConstructor()
        {
            // Generate the argument list that will be passed from the copy ctor to the
            // Init method.
            ExpressionSyntax[] initArguments = GetPropertyNames()
                .Select(name => 
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(OtherParameter),
                        SyntaxFactory.IdentifierName(name)))
                    .ToArray();

            return SyntaxFactory.ConstructorDeclaration(
                default(SyntaxList<AttributeListSyntax>),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                SyntaxFactory.Identifier(TypeName),
                SyntaxFactory.ParameterList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Parameter(
                            default(SyntaxList<AttributeListSyntax>),
                            default(SyntaxTokenList),
                            SyntaxFactory.ParseTypeName(TypeName),
                            SyntaxFactory.Identifier(OtherParameter),
                            default(EqualsValueClauseSyntax)))),
                default(ConstructorInitializerSyntax),
                SyntaxFactory.Block(
                    SyntaxFactory.IfStatement(
                        SyntaxHelper.IsNull(OtherParameter),
                        SyntaxFactory.Block(
                            SyntaxFactory.ThrowStatement(
                                SyntaxFactory.ObjectCreationExpression(
                                    SyntaxFactory.ParseTypeName("ArgumentNullException"),
                                    SyntaxHelper.ArgumentList(
                                        SyntaxFactory.InvocationExpression(
                                            SyntaxFactory.IdentifierName("nameof"),
                                            SyntaxHelper.ArgumentList(
                                                SyntaxFactory.IdentifierName(OtherParameter)))),
                                    default(InitializerExpressionSyntax))))),
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.IdentifierName(InitMethod),
                            SyntaxHelper.ArgumentList(initArguments)))))
            .WithLeadingTrivia(
                SyntaxHelper.MakeDocComment(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.CopyCtorSummary,
                        TypeName),
                    returns: null,
                    paramDescriptionDictionary: new Dictionary<string, string>
                    {
                        [OtherParameter] = Resources.CopyCtorOtherParam
                    },
                    exceptionDictionary: new Dictionary<string, string>
                    {
                        ["ArgumentNullException"] = 
                            string.Format(
                                CultureInfo.CurrentCulture,
                                Resources.CopyCtorOtherNullException,
                                OtherParameter)
                    }));
        }

        private MethodDeclarationSyntax GenerateISyntaxDeepClone()
        {
            return SyntaxFactory.MethodDeclaration(
                default(SyntaxList<AttributeListSyntax>),
                default(SyntaxTokenList), // modifiers
                SyntaxFactory.ParseTypeName(_syntaxInterfaceName),
                SyntaxFactory.ExplicitInterfaceSpecifier(
                    SyntaxFactory.IdentifierName(_syntaxInterfaceName)),
                SyntaxFactory.Identifier(DeepCloneMethod),
                default(TypeParameterListSyntax),
                SyntaxFactory.ParameterList(),
                default(SyntaxList<TypeParameterConstraintClauseSyntax>),
                SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.IdentifierName(DeepCloneCoreMethod),
                            SyntaxHelper.ArgumentList()))),
                default(SyntaxToken));
        }

        private MethodDeclarationSyntax GenerateDeepClone()
        {
            return SyntaxFactory.MethodDeclaration(
                default(SyntaxList<AttributeListSyntax>),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                SyntaxFactory.ParseTypeName(TypeName),
                default(ExplicitInterfaceSpecifierSyntax),
                SyntaxFactory.Identifier(DeepCloneMethod),
                default(TypeParameterListSyntax),
                SyntaxFactory.ParameterList(),
                default(SyntaxList<TypeParameterConstraintClauseSyntax>),
                SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(
                        SyntaxFactory.CastExpression(
                            SyntaxFactory.ParseTypeName(TypeName),
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.IdentifierName(DeepCloneCoreMethod),
                                SyntaxHelper.ArgumentList())))),
                default(SyntaxToken))
                .WithLeadingTrivia(SyntaxHelper.MakeDocComment(Resources.DeepCloneDescription));
        }

        private MethodDeclarationSyntax GenerateDeepCloneCore()
        {
            return SyntaxFactory.MethodDeclaration(
                default(SyntaxList<AttributeListSyntax>),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)),
                SyntaxFactory.ParseTypeName(_syntaxInterfaceName),
                default(ExplicitInterfaceSpecifierSyntax),
                SyntaxFactory.Identifier("DeepCloneCore"),
                default(TypeParameterListSyntax),
                SyntaxFactory.ParameterList(),
                default(SyntaxList<TypeParameterConstraintClauseSyntax>),
                SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName(TypeName),
                            SyntaxHelper.ArgumentList(SyntaxFactory.ThisExpression()),
                            default(InitializerExpressionSyntax)))),
                default(SyntaxToken));
        }

        private MethodDeclarationSyntax GenerateInitMethod()
        {
            SeparatedSyntaxList<ParameterSyntax> parameterList = GenerateInitMethodParameterList();

            return SyntaxFactory.MethodDeclaration(
                default(SyntaxList<AttributeListSyntax>),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)),
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                default(ExplicitInterfaceSpecifierSyntax),
                SyntaxFactory.Identifier(InitMethod),
                default(TypeParameterListSyntax),
                SyntaxFactory.ParameterList(parameterList),
                default(SyntaxList<TypeParameterConstraintClauseSyntax>),
                SyntaxFactory.Block(GenerateInitializations()),
                default(SyntaxToken));
        }

        private SeparatedSyntaxList<ParameterSyntax> GenerateInitMethodParameterList()
        {
            IEnumerable<ParameterSyntax> parameters = GetPropertyNames()
                .Select(name => SyntaxFactory.Parameter(
                    default(SyntaxList<AttributeListSyntax>),
                    default(SyntaxTokenList), // modifiers
                    GetParameterListType(name),
                    SyntaxFactory.Identifier(name.ToCamelCase()),
                    default(EqualsValueClauseSyntax)));

            return SyntaxFactory.SeparatedList(parameters);
        }

        private StatementSyntax[] GenerateInitializations()
        {
            var statements = new List<StatementSyntax>();

            foreach (string propertyName in GetPropertyNames())
            {
                StatementSyntax statement = GenerateInitialization(propertyName);
                if (statement != null)
                {
                    statements.Add(statement);
                }
            }

            return statements.ToArray();
        }

        private StatementSyntax GenerateInitialization(string propertyName)
        {
            PropertyInfo info = PropertyInfoDictionary[propertyName.ToCamelCase()];
            switch (info.InitializationKind)
            {
                case InitializationKind.SimpleAssign:
                    return GenerateSimpleAssignmentInitialization(propertyName);

                case InitializationKind.Collection:
                    return GenerateCollectionAssignment(propertyName);

                case InitializationKind.Uri:
                    return GenerateUriInitialization(propertyName);

                default:
                    // Do not generate initialization code for this property.
                    return null;
            }
        }

        private StatementSyntax GenerateSimpleAssignmentInitialization(string propertyName)
        {
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(propertyName),
                    SyntaxFactory.IdentifierName(propertyName.ToCamelCase())));
        }

        private StatementSyntax GenerateCollectionAssignment(string propertyName)
        {
            string argName = propertyName.ToCamelCase();

            return SyntaxFactory.IfStatement(
                SyntaxHelper.IsNotNull(argName),
                SyntaxFactory.Block());
        }

        private StatementSyntax GenerateUriInitialization(string propertyName)
        {
            PropertyInfo info = PropertyInfoDictionary[propertyName.ToCamelCase()];
            TypeSyntax type = info.Type;

            string uriArgName = propertyName.ToCamelCase();

            return SyntaxFactory.IfStatement(
                SyntaxHelper.IsNotNull(uriArgName),
                SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(propertyName),
                            SyntaxFactory.ObjectCreationExpression(
                                type,
                                SyntaxHelper.ArgumentList(
                                    SyntaxFactory.MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName(uriArgName),
                                        SyntaxFactory.IdentifierName("OriginalString")),
                                            SyntaxFactory.ConditionalExpression(
                                                SyntaxFactory.MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    SyntaxFactory.IdentifierName(uriArgName),
                                                    SyntaxFactory.IdentifierName("IsAbsoluteUri")),
                                                SyntaxFactory.MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    SyntaxFactory.IdentifierName("UriKind"),
                                                    SyntaxFactory.IdentifierName("Absolute")),
                                                SyntaxFactory.MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    SyntaxFactory.IdentifierName("UriKind"),
                                                    SyntaxFactory.IdentifierName("Relative")))),
                                default(InitializerExpressionSyntax))))));
        }

        private MemberDeclarationSyntax OverrideObjectEquals()
        {
            _variableCount = 0;

            return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                EqualsMethod)
                .WithModifiers(
                    SyntaxFactory.TokenList(
                        SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                        SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Parameter(
                                default(SyntaxList<AttributeListSyntax>),
                                default(SyntaxTokenList), // modifiers
                                SyntaxFactory.PredefinedType(
                                    SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
                                SyntaxFactory.Identifier(OtherParameter),
                                default(EqualsValueClauseSyntax)))))
                .WithBody(
                    SyntaxFactory.Block(
                        SyntaxFactory.ReturnStatement(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.IdentifierName(EqualsMethod),
                                SyntaxHelper.ArgumentList(
                                    SyntaxFactory.BinaryExpression(
                                        SyntaxKind.AsExpression,
                                        SyntaxFactory.IdentifierName(OtherParameter),
                                        SyntaxFactory.ParseTypeName(TypeName)))))));

        }
        private MemberDeclarationSyntax OverrideGetHashCode(JsonSchema schema)
        {
            _variableCount = 0;

            return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                GetHashCodeMethod)
                .WithModifiers(
                    SyntaxFactory.TokenList(
                        SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                        SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
                .WithBody(
                    SyntaxFactory.Block(MakeHashCodeContributions(schema)));

        }

        private StatementSyntax[] MakeHashCodeContributions(JsonSchema schema)
        {
            var statements = new List<StatementSyntax>();

            statements.Add(SyntaxFactory.LocalDeclarationStatement(
                            SyntaxFactory.VariableDeclaration(
                                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.VariableDeclarator(
                                        SyntaxFactory.Identifier(GetHashCodeResultVariableName),
                                        default(BracketedArgumentListSyntax),
                                        SyntaxFactory.EqualsValueClause(
                                            SyntaxFactory.LiteralExpression(
                                                SyntaxKind.NumericLiteralExpression,
                                                SyntaxFactory.Literal(GetHashCodeSeedValue))))))));

            if (schema.Properties != null)
            {
                var uncheckedStatements = new List<StatementSyntax>();
                foreach (var property in schema.Properties)
                {
                    string hashKindKey = property.Key;
                    string propName = property.Key.ToPascalCase();

                    uncheckedStatements.Add(
                        MakeHashCodeContribution(hashKindKey, SyntaxFactory.IdentifierName(propName)));
                }

                statements.Add(SyntaxFactory.CheckedStatement(
                    SyntaxKind.UncheckedStatement,
                    SyntaxFactory.Block(uncheckedStatements)));
            }

            statements.Add(SyntaxFactory.ReturnStatement(
                                SyntaxFactory.IdentifierName(GetHashCodeResultVariableName)));

            return statements.ToArray();
        }

        private StatementSyntax MakeHashCodeContribution(string hashKindKey, ExpressionSyntax expression)
        {
            HashKind hashKind = PropertyInfoDictionary[hashKindKey].HashKind;
            switch (hashKind)
            {
                case HashKind.ScalarValueType:
                    return MakeScalarHashCodeContribution(expression);

                case HashKind.ScalarReferenceType:
                    return MakeScalarReferenceTypeHashCodeContribution(expression);

                case HashKind.Collection:
                    return MakeCollectionHashCodeContribution(hashKindKey, expression);

                case HashKind.Dictionary:
                    return MakeDictionaryHashCodeContribution(expression); // TODO: Dictionary as array element; array element as dictionary.

                default:
                    throw new ArgumentException($"Property {hashKindKey} has unknown comparison type {hashKind}.");
            }
        }

        private StatementSyntax MakeScalarHashCodeContribution(ExpressionSyntax expression)
        {
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(GetHashCodeResultVariableName),
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.AddExpression,
                            SyntaxFactory.ParenthesizedExpression(
                                SyntaxFactory.BinaryExpression(
                                    SyntaxKind.MultiplyExpression,
                                    SyntaxFactory.IdentifierName(GetHashCodeResultVariableName),
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.NumericLiteralExpression,
                                        SyntaxFactory.Literal(GetHashCodeCombiningValue)))),
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    expression,
                                    SyntaxFactory.IdentifierName(GetHashCodeMethod))))));
        }

        private StatementSyntax MakeScalarReferenceTypeHashCodeContribution(ExpressionSyntax expression)
        {
            return SyntaxFactory.IfStatement(
                SyntaxHelper.IsNotNull(expression),
                SyntaxFactory.Block(MakeScalarHashCodeContribution(expression)));
        }

        private StatementSyntax MakeCollectionHashCodeContribution(
            string hashKindKey,
            ExpressionSyntax expression)
        {
            string loopVariableName = GetNextVariableName();

            // From the type of the element (primitive, object, list, or dictionary), create
            // the appropriate hash generation code.
            string elementHashTypeKey = MakeElementKeyName(hashKindKey);

            StatementSyntax hashCodeContribution =
                MakeHashCodeContribution(
                    elementHashTypeKey,
                    SyntaxFactory.IdentifierName(loopVariableName));

            return SyntaxFactory.IfStatement(
                SyntaxHelper.IsNotNull(expression),
                SyntaxFactory.Block(
                    SyntaxFactory.ForEachStatement(
                        Var(),
                        loopVariableName,
                        expression,
                        SyntaxFactory.Block(
                            SyntaxFactory.ExpressionStatement(
                                SyntaxFactory.AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    SyntaxFactory.IdentifierName(GetHashCodeResultVariableName),
                                    SyntaxFactory.BinaryExpression(
                                        SyntaxKind.MultiplyExpression,
                                        SyntaxFactory.IdentifierName(GetHashCodeResultVariableName),
                                        SyntaxFactory.LiteralExpression(
                                            SyntaxKind.NumericLiteralExpression,
                                            SyntaxFactory.Literal(GetHashCodeCombiningValue))))),
                            hashCodeContribution))));
        }

        private StatementSyntax MakeDictionaryHashCodeContribution(ExpressionSyntax expression)
        {
            string xorValueVariableName = GetNextVariableName();
            string loopVariableName = GetNextVariableName();

            return SyntaxFactory.IfStatement(
                SyntaxHelper.IsNotNull(expression),
                SyntaxFactory.Block(
                    SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(
                                    SyntaxFactory.Identifier(xorValueVariableName),
                                    default(BracketedArgumentListSyntax),
                                    SyntaxFactory.EqualsValueClause(
                                        SyntaxFactory.LiteralExpression(
                                            SyntaxKind.NumericLiteralExpression,
                                            SyntaxFactory.Literal(0)))))))
                        .WithLeadingTrivia(
                            SyntaxFactory.ParseLeadingTrivia("// Use xor for dictionaries to be order-independent.\n")),
                    SyntaxFactory.ForEachStatement(
                        Var(),
                        loopVariableName,
                        expression,
                        SyntaxFactory.Block(
                            Xor(xorValueVariableName, loopVariableName, "Key"),
                            Xor(xorValueVariableName, loopVariableName, "Value")))));
        }

        private StatementSyntax Xor(string xorValueVariableName, string loopVariableName, string keyValuePairMemberName)
        {
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.ExclusiveOrAssignmentExpression,
                    SyntaxFactory.IdentifierName(xorValueVariableName),
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.ParenthesizedExpression(
                                SyntaxFactory.BinaryExpression(
                                    SyntaxKind.CoalesceExpression,
                                    SyntaxFactory.MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName(loopVariableName),
                                        SyntaxFactory.IdentifierName(keyValuePairMemberName)),
                                    SyntaxFactory.MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName("string"),
                                        SyntaxFactory.IdentifierName("Empty")))),
                            SyntaxFactory.IdentifierName(GetHashCodeMethod)))));
        }

        private MemberDeclarationSyntax ImplementIEquatableEquals(JsonSchema schema)
        {
            _variableCount = 0;

            return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), EqualsMethod)
                .WithModifiers(
                    SyntaxFactory.TokenList(
                        SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Parameter(
                                default(SyntaxList<AttributeListSyntax>),
                                default(SyntaxTokenList), // modifiers
                                SyntaxFactory.ParseTypeName(TypeName),
                                SyntaxFactory.Identifier(OtherParameter),
                                default(EqualsValueClauseSyntax))
                        )))
                .WithBody(
                    SyntaxFactory.Block(GenerateEqualityTests(schema)));
        }

        private StatementSyntax[] GenerateEqualityTests(JsonSchema schema)
        {
            var statements = new List<StatementSyntax>();

            statements.Add(
                SyntaxFactory.IfStatement(
                    SyntaxHelper.IsNull(OtherParameter),
                    SyntaxFactory.Block(SyntaxHelper.Return(false))));

            if (schema.Properties != null)
            {
                foreach (var property in schema.Properties)
                {
                    string comparisonKindKey = property.Key;
                    string propName = property.Key.ToPascalCase();

                    statements.Add(
                        MakeComparisonTest(
                            comparisonKindKey,
                            SyntaxFactory.IdentifierName(propName),
                            OtherPropName(propName)));
                }
            }

            // All comparisons succeeded.
            statements.Add(SyntaxHelper.Return(true));

            return statements.ToArray();
        }

        private IfStatementSyntax MakeComparisonTest(
            string comparisonKindKey,
            ExpressionSyntax left,
            ExpressionSyntax right)
       {
            ComparisonKind comparisonKind = PropertyInfoDictionary[comparisonKindKey].ComparisonKind;
            switch (comparisonKind)
            {
                case ComparisonKind.OperatorEquals:
                    return MakeOperatorEqualsTest(left, right);

                case ComparisonKind.ObjectEquals:
                    return MakeObjectEqualsTest(left, right);

                case ComparisonKind.Collection:
                    return MakeCollectionEqualsTest(comparisonKindKey, left, right);

                case ComparisonKind.Dictionary:
                    return MakeDictionaryEqualsTest(left, right); // TODO: Dictionary as array element; array element as dictionary.

                default:
                    throw new ArgumentException($"Property {comparisonKindKey} has unknown comparison type {comparisonKind}.");
            }
        }

        private IfStatementSyntax MakeOperatorEqualsTest(ExpressionSyntax left, ExpressionSyntax right)
        {
            return SyntaxFactory.IfStatement(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.NotEqualsExpression,
                    left,
                    right),
                SyntaxFactory.Block(SyntaxHelper.Return(false)));
        }

        private IfStatementSyntax MakeObjectEqualsTest(ExpressionSyntax left, ExpressionSyntax right)
        {
            return SyntaxFactory.IfStatement(
                // if (!(Object.Equals(Prop, other.Prop))
                SyntaxFactory.PrefixUnaryExpression(
                    SyntaxKind.LogicalNotExpression,
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(ObjectType),
                            SyntaxFactory.IdentifierName(EqualsMethod)),
                        SyntaxHelper.ArgumentList(left, right))),
                SyntaxFactory.Block(SyntaxHelper.Return(false)));
        }

        private IfStatementSyntax MakeCollectionEqualsTest(
            string comparisonKindKey,
            ExpressionSyntax left,
            ExpressionSyntax right)
        {
            return SyntaxFactory.IfStatement(
                // if (!Object.ReferenceEquals(Prop, other.Prop))
                SyntaxHelper.AreDifferentObjects(left, right),
                SyntaxFactory.Block(
                    // if (Prop == null || other.Prop == null)
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.LogicalOrExpression,
                            SyntaxHelper.IsNull(left),
                            SyntaxHelper.IsNull(right)),
                        SyntaxFactory.Block(SyntaxHelper.Return(false))),

                    // if (Prop.Count != other.Prop.Count)
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.NotEqualsExpression,
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                left,
                                CountPropertyName()),
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                right,
                                CountPropertyName())),
                        SyntaxFactory.Block(SyntaxHelper.Return(false))),

                    CollectionIndexLoop(comparisonKindKey, left, right)
                    ));
        }

        private ForStatementSyntax CollectionIndexLoop(
            string comparisonKindKey,
            ExpressionSyntax left,
            ExpressionSyntax right)
        {
            // The name of the index variable used in the loop over elements.
            string indexVarName = GetNextVariableName();

            // The two elements that will be compared each time through the loop.
            ExpressionSyntax leftElement =
                SyntaxFactory.ElementAccessExpression(
                    left,
                    SyntaxHelper.BracketedArgumentList(
                        SyntaxFactory.IdentifierName(indexVarName)));

            ExpressionSyntax rightElement =
                SyntaxFactory.ElementAccessExpression(
                right,
                SyntaxHelper.BracketedArgumentList(
                    SyntaxFactory.IdentifierName(indexVarName)));

            // From the type of the element (primitive, object, list, or dictionary), create
            // the appropriate comparison, for example, "a == b", or "Object.Equals(a, b)".
            string elmentComparisonTypeKey = MakeElementKeyName(comparisonKindKey);

            IfStatementSyntax comparisonStatement = MakeComparisonTest(elmentComparisonTypeKey, leftElement, rightElement);

            return SyntaxFactory.ForStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(IntTypeAlias),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(
                            SyntaxFactory.Identifier(indexVarName),
                            default(BracketedArgumentListSyntax),
                            SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.LiteralExpression(
                                    SyntaxKind.NumericLiteralExpression,
                                    SyntaxFactory.Literal(0)))))),
                SyntaxFactory.SeparatedList<ExpressionSyntax>(),
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.LessThanExpression,
                    SyntaxFactory.IdentifierName(indexVarName),
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        left,
                        CountPropertyName())),
                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                    SyntaxFactory.PrefixUnaryExpression(
                        SyntaxKind.PreIncrementExpression,
                        SyntaxFactory.IdentifierName(indexVarName))),
                SyntaxFactory.Block(comparisonStatement));
        }

        private string GetNextVariableName()
        {
            return TempVariableNameBase + _variableCount++;
        }

        private IfStatementSyntax MakeDictionaryEqualsTest(ExpressionSyntax left, ExpressionSyntax right)
        {
            string loopVariableName = GetNextVariableName();
            string otherPropertyVariableName = GetNextVariableName();

            return SyntaxFactory.IfStatement(
                SyntaxHelper.AreDifferentObjects(left, right),
                SyntaxFactory.Block(
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.LogicalOrExpression,
                            SyntaxHelper.IsNull(left),
                            SyntaxFactory.BinaryExpression(
                                SyntaxKind.LogicalOrExpression,
                                SyntaxHelper.IsNull(right),
                                SyntaxFactory.BinaryExpression(
                                    SyntaxKind.NotEqualsExpression,
                                    SyntaxFactory.MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        left,
                                        CountPropertyName()),
                                    SyntaxFactory.MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        right,
                                        CountPropertyName())))),
                        SyntaxFactory.Block(SyntaxHelper.Return(false))),
                    SyntaxFactory.ForEachStatement(
                        Var(),
                        loopVariableName,
                        left,
                        SyntaxFactory.Block(
                            SyntaxFactory.LocalDeclarationStatement(
                                default(SyntaxTokenList), // modifiers
                                SyntaxFactory.VariableDeclaration(
                                    SyntaxFactory.ParseTypeName("string"), // TODO: How to get the real type of the dictionary value?
                                    SyntaxFactory.SingletonSeparatedList(
                                        SyntaxFactory.VariableDeclarator(otherPropertyVariableName)))),
                            SyntaxFactory.IfStatement(
                                SyntaxFactory.PrefixUnaryExpression(
                                    SyntaxKind.LogicalNotExpression,
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            right,
                                            SyntaxFactory.IdentifierName("TryGetValue")),
                                        SyntaxFactory.ArgumentList(
                                            SyntaxFactory.SeparatedList(
                                                new ArgumentSyntax[]
                                                {
                                                    SyntaxFactory.Argument(
                                                        SyntaxFactory.MemberAccessExpression(
                                                            SyntaxKind.SimpleMemberAccessExpression,
                                                            SyntaxFactory.IdentifierName(loopVariableName),
                                                            SyntaxFactory.IdentifierName("Key"))),
                                                    SyntaxFactory.Argument(
                                                        default(NameColonSyntax),
                                                        SyntaxFactory.Token(SyntaxKind.OutKeyword),
                                                        SyntaxFactory.IdentifierName(otherPropertyVariableName))

                                                })))),
                                SyntaxFactory.Block(SyntaxHelper.Return(false))),
                            SyntaxFactory.IfStatement(
                                SyntaxFactory.BinaryExpression(
                                    SyntaxKind.NotEqualsExpression,
                                    SyntaxFactory.MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName(loopVariableName),
                                        SyntaxFactory.IdentifierName("Value")),
                                    SyntaxFactory.IdentifierName(otherPropertyVariableName)),
                                SyntaxFactory.Block(SyntaxHelper.Return(false))
                                )))));
        }

#region Syntax helpers

        private TypeSyntax Var()
        {
            return SyntaxFactory.ParseTypeName("var");
        }

        private SimpleNameSyntax CountPropertyName()
        {
            return SyntaxFactory.IdentifierName(CountProperty);
        }

        protected override SyntaxTokenList CreatePropertyModifiers()
        {
            var modifiers = SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
            if (_baseInterfaceName != null)
            {
                modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
            }

            return modifiers;
        }

        private ExpressionSyntax OtherPropName(string propName)
        {
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(OtherParameter),
                SyntaxFactory.IdentifierName(propName));
        }

#endregion Syntax helpers
    }
}
