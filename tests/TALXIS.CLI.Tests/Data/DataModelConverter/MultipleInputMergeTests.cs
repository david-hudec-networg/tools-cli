using System.Linq;
using System.Xml.Linq;
using TALXIS.CLI.Features.Data.DataModelConverter;
using Model = TALXIS.CLI.Features.Data.DataModelConverter.Model;
using Xunit;

namespace TALXIS.CLI.Tests.Data.DataModelConverter;

public class MultipleInputMergeTests
{
    private static XElement Entity(string logicalName, params string[] attributes) =>
        XElement.Parse($"""
            <Entity>
              <Name LocalizedName="{logicalName}" OriginalName="{logicalName}">{logicalName}</Name>
              <EntityInfo>
                <entity Name="{logicalName}">
                  <attributes>
                    {string.Join("", attributes)}
                  </attributes>
                </entity>
              </EntityInfo>
            </Entity>
            """);

    private static string Attr(string name, string type, int? maxLength = null) =>
        $"""<attribute PhysicalName="{name}"><Type>{type}</Type>{(maxLength is null ? "" : $"<MaxLength>{maxLength}</MaxLength>")}</attribute>""";

    private static Model.Module ModuleOf(string name, params XElement[] entities)
    {
        var module = new Model.Module { ModuleName = name };
        module.entities.AddRange(entities);
        return module;
    }

    private static XElement OptionSet(string name, params (int Value, string Label)[] options) =>
        new("optionset",
            new XAttribute("Name", name),
            new XElement("OptionSetType", "picklist"),
            new XElement("options", options.Select(o =>
                new XElement("option",
                    new XAttribute("value", o.Value),
                    new XElement("labels",
                        new XElement("label", new XAttribute("description", o.Label), new XAttribute("languagecode", "1033")))))));

    private static Model.Module WithOptionSets(Model.Module module, params XElement[] optionSets)
    {
        module.optionsets.AddRange(optionSets);
        return module;
    }

    [Fact]
    public void TablesNamedDifferentlyOnlyInCase_MergeIntoOneTable()
    {
        var model = DataModelConverterService.ParseModules(
        [
            ModuleOf("base", Entity("Contoso_Thing", Attr("contoso_a", "nvarchar", 50))),
            ModuleOf("layer", Entity("contoso_thing", Attr("contoso_b", "nvarchar", 50))),
        ]);

        var table = Assert.Single(model.tables);
        var names = table.Rows.Select(r => r.Name).ToList();
        Assert.Contains("contoso_a", names);
        Assert.Contains("contoso_b", names);
    }

    [Theory]
    [InlineData("Contoso_Thing", "contoso_thing")]
    [InlineData("contoso_thing", "Contoso_Thing")]
    public void TablesNamedDifferentlyOnlyInCase_KeepTheFirstSpelling(string first, string second)
    {
        var model = DataModelConverterService.ParseModules(
        [
            ModuleOf("base", Entity(first, Attr("contoso_a", "nvarchar", 50))),
            ModuleOf("layer", Entity(second, Attr("contoso_b", "nvarchar", 50))),
        ]);

        Assert.Equal(first, Assert.Single(model.tables).LogicalName);
    }

    [Fact]
    public void ModuleWithoutAKey_FollowedByOneThatSuppliesIt_YieldsASinglePrimaryKey()
    {
        var model = DataModelConverterService.ParseModules(
        [
            ModuleOf("apps", Entity("contoso_thing", Attr("contoso_a", "nvarchar", 50))),
            ModuleOf("model", Entity("contoso_thing", Attr("activityid", "primarykey"))),
        ]);

        var keys = Assert.Single(model.tables).Rows.Where(r => r.RowType == Model.RowType.Primarykey).ToList();
        Assert.Equal("activityid", Assert.Single(keys).Name);
    }

    [Fact]
    public void TableNoModuleGivesAKey_GetsOneNamedAfterIt()
    {
        var model = DataModelConverterService.ParseModules(
            [ModuleOf("only", Entity("contoso_thing", Attr("contoso_a", "nvarchar", 50)))]);

        var key = Assert.Single(Assert.Single(model.tables).Rows, r => r.RowType == Model.RowType.Primarykey);
        Assert.Equal("contoso_thingid", key.Name);
    }

    [Fact]
    public void TableIsCreditedToTheModuleDeclaringMostOfItsAttributes()
    {
        var model = DataModelConverterService.ParseModules(
        [
            ModuleOf("apps", Entity("contoso_thing")),
            ModuleOf("extension", Entity("contoso_thing", Attr("contoso_x", "int"))),
            ModuleOf("model", Entity("contoso_thing", Attr("contoso_a", "int"), Attr("contoso_b", "int"), Attr("contoso_c", "int"))),
        ]);

        Assert.Equal("model", Assert.Single(model.tables).ParentModule.ModuleName);
        Assert.Contains("//model", DataModelConverterService.ConvertToDBML(model));
    }

    [Fact]
    public void ModulesDeclaringEquallyManyAttributes_CreditTheEarlierOne()
    {
        var model = DataModelConverterService.ParseModules(
        [
            ModuleOf("first", Entity("contoso_thing", Attr("contoso_a", "int"))),
            ModuleOf("second", Entity("contoso_thing", Attr("contoso_b", "int"))),
        ]);

        Assert.Equal("first", Assert.Single(model.tables).ParentModule.ModuleName);
    }

    [Fact]
    public void TableNoModuleGivesAttributes_IsCreditedToTheFirstModuleDeclaringIt()
    {
        var model = DataModelConverterService.ParseModules(
        [
            ModuleOf("first", Entity("contoso_thing")),
            ModuleOf("second", Entity("contoso_thing")),
        ]);

        Assert.Equal("first", Assert.Single(model.tables).ParentModule.ModuleName);
    }

    [Theory]
    [InlineData("One", "Uno", "One")]
    [InlineData("Uno", "One", "Uno")]
    public void OptionSetLabelsDisagreeing_KeepTheFirstModulesLabel(string first, string second, string expected)
    {
        var model = DataModelConverterService.ParseModules(
        [
            WithOptionSets(ModuleOf("first"), OptionSet("contoso_status", (1, first))),
            WithOptionSets(ModuleOf("second"), OptionSet("contoso_status", (1, second), (2, "Two"))),
        ]);

        var optionSet = Assert.Single(model.optionSets);
        Assert.Equal(expected, optionSet.Values.Single(v => v.Value == 1).Label);
        Assert.Contains(optionSet.Values, v => v.Value == 2);
    }
}
