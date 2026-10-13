namespace CellBridge.Lists;

/// <summary>Operator templates are copied into a pinned schema. Instantiation never propagates later template versions.</summary>
public static class ListTemplates
{
    public static void Validate(ListTemplate template, ListLimits limits)
    {
        ListValidation.ValidateDefinition(new ListDefinition(template.Id, template.Id, template.Name, template.Version, template.Fields), limits);
    }

    public static ListDefinition Instantiate(ListTemplate template, Guid workspaceId, Guid listId, string name,
        IReadOnlyList<ListField>? extensions = null, string? numberPrefix = null, ListLimits? limits = null)
    {
        limits ??= new ListLimits();
        Validate(template, limits);
        ListValidation.Require((extensions ?? []).All(f => !f.Required && !f.Protected), "Template extensions must be optional workspace fields.");
        var fields = template.Fields.Select(f => Copy(f with { Protected = true })).Concat((extensions ?? []).Select(Copy)).ToArray();
        var definition = new ListDefinition(listId, workspaceId, name, 1, fields, template.Id, template.Version, numberPrefix);
        ListValidation.ValidateDefinition(definition, limits);
        return definition;
    }

    private static ListField Copy(ListField field) => field with
    {
        Choices = field.Choices?.ToArray(),
        Display = field.Display is null ? null : field.Display with { Tags = field.Display.Tags?.ToArray() }
    };
}
