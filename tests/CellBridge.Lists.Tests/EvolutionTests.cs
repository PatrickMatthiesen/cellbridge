using CellBridge.Lists;
using static CellBridge.Lists.Tests.ValidationTests;

namespace CellBridge.Lists.Tests;

public class EvolutionTests
{
    [Fact]
    public void Optional_additions_and_labels_preserve_old_records()
    {
        var previous = Definition();
        var next = previous with { SchemaVersion = 2, Name = "Renamed", Fields = [previous.Fields[0] with { Name = "Renamed value", Display = new(HelpText: "Plain help", Order: 5, Tags: ["customer"]) }, new ListField(Guid.NewGuid(), "Custom", ListFieldType.Integer)] };
        ListValidation.ValidateEvolution(previous, next, new ListLimits());
        ListValidation.ValidateValues(next, Values("\"unchanged\""), new ListLimits());
    }

    [Fact]
    public void Published_constraints_provenance_and_ownership_are_immutable()
    {
        var previous = Definition();
        var next = previous with { SchemaVersion = 2 };
        foreach (var field in new[]
        {
            previous.Fields[0] with { Required = true }, previous.Fields[0] with { Type = ListFieldType.Integer },
            previous.Fields[0] with { MaxLength = 2000 }, previous.Fields[0] with { Protected = true }
        }) Reject(() => ListValidation.ValidateEvolution(previous, next with { Fields = [field] }, new ListLimits()));
        Reject(() => ListValidation.ValidateEvolution(previous, next with { Fields = [] }, new ListLimits()));
        Reject(() => ListValidation.ValidateEvolution(previous, next with { SchemaVersion = 3 }, new ListLimits()));
        Reject(() => ListValidation.ValidateEvolution(previous, next with { WorkspaceId = Guid.NewGuid() }, new ListLimits()));
        Reject(() => ListValidation.ValidateEvolution(previous, next with { Fields = [previous.Fields[0], new ListField(Guid.NewGuid(), "Required addition", ListFieldType.Text, true)] }, new ListLimits()));
    }

    [Fact]
    public void Choice_labels_can_change_but_choice_ids_cannot()
    {
        var original = Definition() with { Fields = [new ListField(Field, "Choice", ListFieldType.Choice, Choices: [new("a", "Original")])] };
        var renamed = original with { SchemaVersion = 2, Fields = [original.Fields[0] with { Choices = [new("a", "New label")] }] };
        ListValidation.ValidateEvolution(original, renamed, new ListLimits());
        Reject(() => ListValidation.ValidateEvolution(original, renamed with { Fields = [original.Fields[0] with { Choices = [new("b", "Original")] }] }, new ListLimits()));
    }

    [Fact]
    public void Template_instantiation_pins_copies_and_protects_every_template_field()
    {
        var choices = new List<FieldChoice> { new("a", "First") };
        var template = new ListTemplate(Guid.NewGuid(), "Company X", 3, [new ListField(Field, "Choice", ListFieldType.Choice, Choices: choices)]);
        var instantiated = ListTemplates.Instantiate(template, Workspace, List, "Customer X", [new ListField(Guid.NewGuid(), "Extra", ListFieldType.Text)]);
        Assert.Equal(template.Id, instantiated.TemplateId);
        Assert.Equal(3, instantiated.TemplateVersion);
        Assert.True(instantiated.Fields[0].Protected);
        Assert.False(instantiated.Fields[1].Protected);
        choices.Add(new("b", "Second"));
        Assert.Single(instantiated.Fields[0].Choices!);
        Reject(() => ListValidation.ValidateEvolution(instantiated, instantiated with { SchemaVersion = 2, Fields = [instantiated.Fields[0] with { Name = "Edit protected" }, instantiated.Fields[1]] }, new ListLimits()));
        Reject(() => ListValidation.ValidateEvolution(instantiated, instantiated with { SchemaVersion = 2, Fields = [instantiated.Fields[0] with { Display = new(Hidden: true) }, instantiated.Fields[1]] }, new ListLimits()));
        Reject(() => ListTemplates.Instantiate(template, Workspace, List, "Customer", [new ListField(Guid.NewGuid(), "Invalid", ListFieldType.Text, Protected: true)]));
    }
}
