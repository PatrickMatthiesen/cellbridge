using System.Text.Json.Nodes;

namespace CellBridge.Lists;

/// <summary>Three-way comparison only. The store authorizes and commits against CurrentRevision separately.</summary>
public static class ListMerge
{
    public static MergePreview Preview(ListDefinition definition, ListRecord current, MergeRequest request, ListLimits limits)
    {
        if (request.SchemaVersion != definition.SchemaVersion)
            throw new ListException(ListFailure.Conflict, "The current schema version is required.");
        ListValidation.Require(current.ListId == definition.Id && current.WorkspaceId == definition.WorkspaceId, "Record does not belong to the definition.");
        ListValidation.ValidateValues(definition, request.OriginalValues, limits);
        ListValidation.ValidateValues(definition, current.Values, limits);
        ListValidation.ValidateChanges(definition, request.Changes, limits);
        var proposed = ListValidation.ApplyChanges(definition, request.OriginalValues, request.Changes, limits);
        var fields = definition.Fields.ToDictionary(f => f.Id);
        var results = new List<FieldMerge>();
        var suggested = new List<FieldChange>();
        var changes = request.Changes.ToDictionary(change => change.FieldId);
        var previewIds = request.Changes.Select(change => change.FieldId).Concat(definition.Fields
            .Where(field =>
            {
                var key = field.Id.ToString("D");
                var originalPresent = request.OriginalValues.TryGetPropertyValue(key, out var original);
                var currentPresent = current.Values.TryGetPropertyValue(key, out var now);
                return !Equal(field, originalPresent, original, currentPresent, now);
            }).Select(field => field.Id)).Distinct();
        foreach (var fieldId in previewIds)
        {
            var edited = changes.ContainsKey(fieldId);
            var key = fieldId.ToString("D");
            var originalPresent = request.OriginalValues.TryGetPropertyValue(key, out var original);
            var currentPresent = current.Values.TryGetPropertyValue(key, out var now);
            var proposedPresent = proposed.TryGetPropertyValue(key, out var next);
            var field = fields[fieldId];
            var unchanged = Equal(field, originalPresent, original, currentPresent, now);
            var alreadyApplied = Equal(field, currentPresent, now, proposedPresent, next);
            var proposalUnchanged = Equal(field, originalPresent, original, proposedPresent, next);
            var conflict = edited && !unchanged && !alreadyApplied && !proposalUnchanged;
            results.Add(new FieldMerge(fieldId, originalPresent, original?.DeepClone(), currentPresent,
                now?.DeepClone(), proposedPresent, next?.DeepClone(), conflict));
            if (edited && !conflict && !alreadyApplied && !proposalUnchanged)
                suggested.Add(new FieldChange(fieldId, next?.DeepClone(), !proposedPresent));
        }
        // Nonconflicting edits must still fit the final record budget.
        ListValidation.ApplyChanges(definition, current.Values, suggested, limits);
        return new MergePreview(current.Revision, definition.SchemaVersion, suggested, results);
    }

    private static bool Equal(ListField field, bool leftPresent, JsonNode? left, bool rightPresent, JsonNode? right) =>
        leftPresent == rightPresent && Equals(ListValidation.Scalar(field, left), ListValidation.Scalar(field, right));
}
