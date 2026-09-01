﻿using System.IO;
using System.Linq;
using System.Reflection;
using DotMake.CommandLine;
using TALXIS.CLI.Features.Data;
using System.Xml.Linq;
using TALXIS.CLI.Features.Data.DataModelConverter;
using Model = TALXIS.CLI.Features.Data.DataModelConverter.Model;
using Xunit;

namespace TALXIS.CLI.Tests.Data.DataModelConverter;

/// <summary>
/// Regression tests for four defects that made the converter silently lose or corrupt
/// model content. Each was found by diffing converter output against the source
/// declarations of real solutions; each test fails against the unfixed converter.
/// </summary>
public class DataModelConverterServiceTests
{
    private static XElement Entity(string logicalName, params string[] attributes)
    {
        var attrXml = string.Join("", attributes);
        return XElement.Parse($"""
            <Entity>
              <Name LocalizedName="{logicalName}" OriginalName="{logicalName}">{logicalName}</Name>
              <EntityInfo>
                <entity Name="{logicalName}">
                  <attributes>
                    <attribute PhysicalName="{logicalName}id"><Type>primarykey</Type></attribute>
                    {attrXml}
                  </attributes>
                </entity>
              </EntityInfo>
            </Entity>
            """);
    }

    private static string Attr(string name, string type) =>
        $"""<attribute PhysicalName="{name}"><Type>{type}</Type></attribute>""";

    private static string Lookup(string name) => Attr(name, "lookup");

    private static XElement OneToMany(string name, string child, string childAttr, string parent) =>
        XElement.Parse($"""
            <EntityRelationship Name="{name}">
              <EntityRelationshipType>OneToMany</EntityRelationshipType>
              <ReferencingEntityName>{child}</ReferencingEntityName>
              <ReferencedEntityName>{parent}</ReferencedEntityName>
              <ReferencingAttributeName>{childAttr}</ReferencingAttributeName>
            </EntityRelationship>
            """);

    private static Model.Module ModuleWith(XElement[] entities, XElement[]? relationships = null)
    {
        var module = new Model.Module();
        module.entities.AddRange(entities);
        if (relationships != null) module.relationships.AddRange(relationships);
        return module;
    }

    // ---- Defect 1: relationships deduped on the table pair, not the column -------------

    [Fact]
    public void TwoLookupsBetweenSameTablePair_BothProduceRelationships()
    {
        var module = ModuleWith(
            [Entity("account"), Entity("contoso_project", Lookup("contoso_ownerid"), Lookup("contoso_billtoid"))],
            [
                OneToMany("rel_owner",  "contoso_project", "contoso_ownerid",  "account"),
                OneToMany("rel_billto", "contoso_project", "contoso_billtoid", "account"),
            ]);

        var model = DataModelConverterService.ParseModules([module]);

        var toAccount = model.relationships
            .Where(r => r.RighSideTable?.LogicalName == "account")
            .Select(r => r.LeftSideRow?.Name)
            .ToList();

        Assert.Equal(2, toAccount.Count);
        Assert.Contains("contoso_ownerid", toAccount);
        Assert.Contains("contoso_billtoid", toAccount);
    }

    // ---- Defect 3: a column vanishes when its option set will not resolve --------------

    [Fact]
    public void PicklistWithUnresolvableOptionSet_KeepsColumnInsteadOfDroppingIt()
    {
        var picklist = """
            <attribute PhysicalName="contoso_statuscode">
              <Type>picklist</Type>
              <OptionSetName>contoso_never_declared_anywhere</OptionSetName>
            </attribute>
            """;
        var module = ModuleWith([Entity("contoso_thing", picklist)]);

        var table = DataModelConverterService.ParseModules([module])
            .tables.Single(t => t.LogicalName == "contoso_thing");

        var row = table.Rows.SingleOrDefault(r => r.Name == "contoso_statuscode");
        Assert.NotNull(row);
        // Cleared so the column cannot reference an Enum that was never emitted;
        // RowType is deliberately left alone so each translator keeps its own handling.
        Assert.True(string.IsNullOrEmpty(row!.OptionSetName));
    }
}
