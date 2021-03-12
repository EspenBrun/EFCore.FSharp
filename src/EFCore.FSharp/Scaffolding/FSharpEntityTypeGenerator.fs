namespace EntityFrameworkCore.FSharp.Scaffolding

open System
open System.Collections.Generic
open System.Reflection
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Design
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Metadata
open Microsoft.EntityFrameworkCore.Metadata.Internal
open Microsoft.EntityFrameworkCore.Scaffolding.Internal
open EntityFrameworkCore.FSharp.EntityFrameworkExtensions
open EntityFrameworkCore.FSharp.IndentedStringBuilderUtilities
open EntityFrameworkCore.FSharp.Internal

module ScaffoldingTypes =
    type RecordOrType = | ClassType | RecordType
    type OptionOrNullable = | OptionTypes | NullableTypes

open ScaffoldingTypes

type internal AttributeWriter(name:string) =
    let parameters = List<string>()
    member __.AddParameter p =
        parameters.Add p
    override __.ToString() =
        if Seq.isEmpty parameters then
            sprintf "[<%s>]" name
        else
            sprintf "[<%s(%s)>]" name (String.Join(", ", parameters))

type FSharpEntityTypeGenerator(code : ICSharpHelper) =
    let createAttributeQuick = AttributeWriter >> string
    let primitiveTypeNames =
        seq {
            yield (typedefof<bool>, "bool")
            yield (typedefof<byte>, "byte")
            yield (typedefof<byte[]>, "byte[]")
            yield (typedefof<sbyte>, "sbyte")
            yield (typedefof<int>, "int")
            yield (typedefof<char>, "char")
            yield (typedefof<float32>, "float32")
            yield (typedefof<double>, "double")
            yield (typedefof<string>, "string")
            yield (typedefof<decimal>, "decimal")
        }
        |> dict

    let rec getTypeName optionOrNullable (t:Type) =

        if t.IsArray then
            (getTypeName optionOrNullable (t.GetElementType())) + "[]"

        else if t.GetTypeInfo().IsGenericType then
            if t.GetGenericTypeDefinition() = typedefof<Nullable<_>> then
                match optionOrNullable with
                | NullableTypes ->  "Nullable<" + (getTypeName optionOrNullable (Nullable.GetUnderlyingType(t))) + ">";
                | OptionTypes -> (getTypeName optionOrNullable (Nullable.GetUnderlyingType(t))) + " option"
            else
                let genericTypeDefName = t.Name.Substring(0, t.Name.IndexOf('`'));
                let genericTypeArguments = String.Join(", ", t.GenericTypeArguments |> Seq.map(fun t' -> getTypeName optionOrNullable t'))
                genericTypeDefName + "<" + genericTypeArguments + ">";

        else
            match primitiveTypeNames.TryGetValue t with
            | true, value -> value
            | _ -> t.Name

    let generatePrimaryKeyAttribute (p:IProperty) sb =

        let key = getPrimaryKey p

        if isNull key || key.Properties.Count <> 1 then
            sb
        else
            sb |> appendLine ("KeyAttribute" |> createAttributeQuick)

    let generateRequiredAttribute (p:IProperty) sb =

        let isNullableOrOptionType (t:Type) =
            let typeInfo = t.GetTypeInfo()
            (typeInfo.IsValueType |> not) ||
                (typeInfo.IsGenericType && (typeInfo.GetGenericTypeDefinition() = typedefof<Nullable<_>> || typeInfo.GetGenericTypeDefinition() = typedefof<Option<_>>))

        if (not p.IsNullable) && (p.ClrType |> isNullableOrOptionType) && (p.IsPrimaryKey() |> not) then
            sb |> appendLine ("RequiredAttribute" |> createAttributeQuick)
        else
            sb

    let generateColumnAttribute (p:IProperty) sb =
        let columnName = p.GetColumnBaseName()
        let columnType = getConfiguredColumnType p

        let delimitedColumnName = if isNull columnName |> not && columnName <> p.Name then FSharpUtilities.delimitString(columnName) |> Some else Option.None
        let delimitedColumnType = if isNull columnType |> not then FSharpUtilities.delimitString(columnType) |> Some else Option.None

        if delimitedColumnName.IsSome || delimitedColumnType.IsSome then
            let a = "ColumnAttribute" |> AttributeWriter

            match delimitedColumnName with
            | Some name -> name |> a.AddParameter
            | None -> ()

            match delimitedColumnType with
            | Some t -> (sprintf "Type = %s" t) |> a.AddParameter
            | None -> ()

            sb |> appendLine (a |> string)

        else
            sb


    let generateMaxLengthAttribute (p:IProperty) sb =

        let ml = p.GetMaxLength()

        if ml.HasValue then
            let attrName =
               if p.ClrType = typedefof<string> then "StringLengthAttribute" else "MaxLengthAttribute"

            let a = AttributeWriter(attrName)
            a.AddParameter (code.Literal ml.Value)

            sb |> append (string a)
        else
            sb

    let generateTableAttribute (entityType : IEntityType) sb =
        sb |> append "// Annotations"

    let generateEntityTypeDataAnnotations entityType sb =
        sb |> generateTableAttribute entityType


    let generateConstructor (entityType : IEntityType) sb =
        sb |> appendLine "new() = { }"

    let generateProperties (entityType : IEntityType) (optionOrNullable:OptionOrNullable) sb =
        // TODO: add key etc.
        let props =
            entityType.GetProperties()
            |> Seq.sortBy ScaffoldingPropertyExtensions.GetColumnOrdinal
        sb |> appendLine "// Properties"

    let generateNavigationProperties (entityType : IEntityType) (optionOrNullable:OptionOrNullable) sb =
        sb |> appendLine "// NavigationProperties"

    let generateClass (entityType : IEntityType) ``namespace`` useDataAnnotations optionOrNullable sb =

        sb
            |>
                if useDataAnnotations then
                    generateEntityTypeDataAnnotations entityType
                else
                    id
            |> appendLine (sprintf "type %s() =" entityType.Name)
            |> indent
            |> generateConstructor entityType
            |> generateProperties entityType optionOrNullable
            |> generateNavigationProperties entityType optionOrNullable
            |> unindent

    let generateRecordTypeEntry useDataAnnotations optionOrNullable (p: IProperty) sb =

        if useDataAnnotations then
            sb
                |> generatePrimaryKeyAttribute p
                |> generateRequiredAttribute p
                |> generateColumnAttribute p
                |> generateMaxLengthAttribute p
                |> ignore

        let typeName = getTypeName optionOrNullable p.ClrType
        sb |> appendLine (sprintf "mutable %s: %s" p.Name typeName) |> ignore
        ()

    let writeRecordProperties (properties :IProperty seq) (useDataAnnotations:bool) (skipFinalNewLine: bool) optionOrNullable sb =
        properties
        |> Seq.iter(fun p -> generateRecordTypeEntry useDataAnnotations optionOrNullable p sb)

        sb

    let generateForeignKeyAttribute (n:INavigation) sb =

        if n.IsOnDependent && n.ForeignKey.PrincipalKey.IsPrimaryKey() then
            let a = "ForeignKeyAttribute" |> AttributeWriter
            let props = n.ForeignKey.Properties |> Seq.map (fun n' -> n'.Name)
            String.Join(",", props) |> FSharpUtilities.delimitString |> a.AddParameter
            sb |> appendLine (a |> string)
        else
            sb

    let generateInversePropertyAttribute (n:INavigation) sb =
        if n.ForeignKey.PrincipalKey.IsPrimaryKey() then
            let inverse = n.Inverse
            if isNull inverse then
                sb
            else
                let a = "InversePropertyAttribute" |> AttributeWriter
                inverse.Name |> FSharpUtilities.delimitString |> a.AddParameter
                sb |> appendLine (a |> string)
        else
            sb

    let generateNavigateTypeEntry (n:INavigation) (useDataAnnotations:bool) (skipFinalNewLine: bool) optionOrNullable sb =
        if useDataAnnotations then
            sb
                |> generateForeignKeyAttribute n
                |> generateInversePropertyAttribute n
                |> ignore

        let referencedTypeName = n.TargetEntityType.Name
        let navigationType =
            if n.IsCollection then
                sprintf "ICollection<%s>" referencedTypeName
            else
                referencedTypeName
        sb |> appendLine (sprintf "mutable %s: %s" n.Name navigationType) |> ignore

    let writeNavigationProperties (nav:INavigation seq) (useDataAnnotations:bool) (skipFinalNewLine: bool) optionOrNullable sb =
        nav |> Seq.iter(fun n -> generateNavigateTypeEntry n useDataAnnotations skipFinalNewLine optionOrNullable sb)
        sb

    let generateRecord (entityType : IEntityType) ``namespace`` (useDataAnnotations:bool) optionOrNullable sb =
        let properties =
            entityType.GetProperties()

        let navProperties =
            entityType
                    |> EntityTypeExtensions.GetNavigations
                    |> Seq.sortBy(fun n -> ((if n.IsOnDependent then 0 else 1), (if n.IsCollection then 1 else 0)))

        let navsIsEmpty = navProperties |> Seq.isEmpty

        sb
            |> appendLine ("CLIMutable" |> createAttributeQuick)
            |> appendLine (sprintf "type %s = {" entityType.Name)
            |> indent
            |> writeRecordProperties properties useDataAnnotations navsIsEmpty optionOrNullable
            |> writeNavigationProperties navProperties useDataAnnotations true optionOrNullable
            |> unindent
            |> appendLine "}"
            |> appendEmptyLine


    let writeCode (entityType: IEntityType) ``namespace`` (useDataAnnotation: bool) createTypesAs optionOrNullable sb =

        let generate =
            match createTypesAs with
            | ClassType -> generateClass
            | RecordType -> generateRecord

        sb
            |> indent
            |> generate entityType ``namespace`` useDataAnnotation optionOrNullable
            |> string

    interface ICSharpEntityTypeGenerator with
        member __.WriteCode(entityType, ``namespace``, useDataAnnotations) =
            writeCode entityType ``namespace`` useDataAnnotations RecordOrType.RecordType OptionOrNullable.OptionTypes (IndentedStringBuilder())
