using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TALXIS.CLI.Features.Data.DataModelConverter;
using TALXIS.CLI.Features.Data.DataModelConverter.AppScope;
using Model = TALXIS.CLI.Features.Data.DataModelConverter.Model;
using Xunit;

namespace TALXIS.CLI.Tests.Data.DataModelConverter;

/// <summary>
/// Narrowing an app's tables to the columns something in it refers to. The rule that must
/// never break: a column an edge depends on stays, because the SQL and EDMX translators
/// read a relationship's endpoints without a null check — dropping one turns a narrower
/// diagram into a crash.
/// </summary>
public class ColumnScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "txc-cols-" + Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void WriteForm(string contents)
    {
        var dir = Path.Combine(_root, "module", "Declarations", "Entities", "contoso_thing", "FormXml", "main");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "form.xml"), contents);
    }

    private void WriteCode(string fileName, string contents)
    {
        var dir = Path.Combine(_root, "module", "Plugins");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), contents);
    }

    private static XElement Entity(string logicalName, params string[] columns) =>
        XElement.Parse($"""
            <Entity>
              <Name LocalizedName="{logicalName}" OriginalName="{logicalName}">{logicalName}</Name>
              <EntityInfo><entity Name="{logicalName}"><attributes>
                <attribute PhysicalName="{logicalName}id"><Type>primarykey</Type></attribute>
                {string.Join("", columns.Select(c => $"""<attribute PhysicalName="{c}"><Type>nvarchar</Type><MaxLength>50</MaxLength></attribute>"""))}
              </attributes></entity></EntityInfo>
            </Entity>
            """);

    private ResolvedAppScope ScopeFor(bool scanCode = false, params string[] tables)
    {
        var scope = new ResolvedAppScope { UniqueName = "contoso_app", FilterColumns = true, ScanCode = scanCode };
        scope.SearchRoots.Add(_root);
        foreach (var t in tables) scope.TableLogicalNames.Add(t);
        return scope;
    }

    private static Model.Table TableIn(Model.ParsedModel m, string name) =>
        m.tables.Single(t => t.LogicalName == name);

    [Fact]
    public void AColumnAFormRefersTo_IsKept_AndOneNothingRefersTo_IsDroppedAndReported()
    {
        WriteForm("""<form><control id="c" datafieldname="contoso_onaform" /></form>""");
        var module = new Model.Module { ModuleName = "m" };
        module.entities.Add(Entity("contoso_thing", "contoso_onaform", "contoso_nowhere"));

        var scope = ScopeFor(false, "contoso_thing");
        var model = DataModelConverterService.ParseModules([module], scope);

        var columns = TableIn(model, "contoso_thing").Rows.Select(r => r.Name).ToList();
        Assert.Contains("contoso_onaform", columns);
        Assert.DoesNotContain("contoso_nowhere", columns);
        Assert.Contains("contoso_thing.contoso_nowhere", scope.DroppedColumns);
    }

    [Fact]
    public void ThePrimaryKeyIsNeverDropped_EvenWhenNothingRefersToIt()
    {
        WriteForm("<form />");
        var module = new Model.Module { ModuleName = "m" };
        module.entities.Add(Entity("contoso_thing", "contoso_nowhere"));

        var model = DataModelConverterService.ParseModules([module], ScopeFor(false, "contoso_thing"));

        Assert.Contains(TableIn(model, "contoso_thing").Rows, r => r.RowType == Model.RowType.Primarykey);
    }

    [Fact]
    public void AColumnAnEdgeDependsOn_SurvivesAndTheSqlAndEdmxTargetsStillRender()
    {
        // The regression that matters: the translators dereference a relationship's
        // endpoints with no null check, so dropping one crashes rather than shrinks.
        WriteForm("<form />");
        var module = new Model.Module { ModuleName = "m" };
        module.entities.Add(Entity("contoso_child", "contoso_lookupid"));
        module.entities.Add(Entity("contoso_parent"));
        module.relationships.Add(XElement.Parse("""
            <EntityRelationship Name="rel">
              <EntityRelationshipType>OneToMany</EntityRelationshipType>
              <ReferencingEntityName>contoso_child</ReferencingEntityName>
              <ReferencedEntityName>contoso_parent</ReferencedEntityName>
              <ReferencingAttributeName>contoso_lookupid</ReferencingAttributeName>
            </EntityRelationship>
            """));

        var model = DataModelConverterService.ParseModules(
            [module], ScopeFor(false, "contoso_child", "contoso_parent"));

        Assert.Contains(TableIn(model, "contoso_child").Rows, r => r.Name == "contoso_lookupid");
        Assert.All(model.relationships, r =>
        {
            Assert.NotNull(r.LeftSideRow);
            Assert.NotNull(r.RighSideRow);
        });

        Assert.Null(Record.Exception(() => DataModelConverterService.ConvertToSQL(model)));
        Assert.Null(Record.Exception(() => DataModelConverterService.ConvertToEDSSQL(model)));
        Assert.Null(Record.Exception(() => DataModelConverterService.ConvertToEDMX(model)));
    }

    [Fact]
    public void AColumnOnlyAPluginMentions_IsDroppedWithoutScanCode_AndKeptWithIt()
    {
        // Plug-in and script sources sit outside the declarations, so they are invisible
        // to the scan unless it is widened — which is the whole reason the flag exists.
        WriteForm("<form />");
        WriteCode("Handler.cs", """var v = entity.GetAttributeValue<string>("contoso_onlyincode");""");

        Model.ParsedModel Convert(bool scanCode)
        {
            var module = new Model.Module { ModuleName = "m" };
            module.entities.Add(Entity("contoso_thing", "contoso_onlyincode"));
            return DataModelConverterService.ParseModules([module], ScopeFor(scanCode, "contoso_thing"));
        }

        Assert.DoesNotContain(TableIn(Convert(false), "contoso_thing").Rows, r => r.Name == "contoso_onlyincode");
        Assert.Contains(TableIn(Convert(true), "contoso_thing").Rows, r => r.Name == "contoso_onlyincode");
    }

    [Fact]
    public void AnEntityDeclarationDoesNotCountAsAReferenceToItsOwnColumns()
    {
        // Scanning Entity.xml would report every column as referenced by its own
        // declaration, which would make the filter a no-op that looks like it works.
        var declarations = Path.Combine(_root, "module", "Declarations", "Entities", "contoso_thing");
        Directory.CreateDirectory(declarations);
        File.WriteAllText(Path.Combine(declarations, "Entity.xml"),
            Entity("contoso_thing", "contoso_nowhere").ToString());

        var module = new Model.Module { ModuleName = "m" };
        module.entities.Add(Entity("contoso_thing", "contoso_nowhere"));

        var model = DataModelConverterService.ParseModules([module], ScopeFor(false, "contoso_thing"));

        Assert.DoesNotContain(TableIn(model, "contoso_thing").Rows, r => r.Name == "contoso_nowhere");
    }

    [Fact]
    public void WithoutTheFlag_ColumnsAreLeftAlone()
    {
        WriteForm("<form />");
        var module = new Model.Module { ModuleName = "m" };
        module.entities.Add(Entity("contoso_thing", "contoso_nowhere"));

        var scope = new ResolvedAppScope { UniqueName = "contoso_app" };
        scope.TableLogicalNames.Add("contoso_thing");

        var model = DataModelConverterService.ParseModules([module], scope);

        Assert.Contains(TableIn(model, "contoso_thing").Rows, r => r.Name == "contoso_nowhere");
        Assert.Empty(scope.DroppedColumns);
    }
}
