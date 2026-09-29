namespace OfficeInspectors.Adapter;

public sealed class OfficeInspectorsFactAttribute : FactAttribute
{
    public OfficeInspectorsFactAttribute()
    {
#if !OFFICE_INSPECTORS
        Skip = "Office Inspectors sources unavailable. Set OfficeInspectorsRoot to enable the parser checks.";
#endif
    }
}
