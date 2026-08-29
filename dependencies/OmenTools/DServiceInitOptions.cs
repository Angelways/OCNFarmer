using OmenTools.OmenService.Abstractions;

namespace OmenTools;

public sealed class DServiceInitOptions
{
    private readonly HashSet<Type> disabledServices = [];
    private HashSet<Type>? enabledServices;

    public DServiceInitOptions EnableOnly(params Type[] serviceTypes)
    {
        enabledServices = serviceTypes.ToHashSet();
        return this;
    }

    public DServiceInitOptions Disable<TService>() where TService : OmenServiceBase
    {
        disabledServices.Add(typeof(TService));
        return this;
    }

    internal bool IsDisabled(Type serviceType) =>
        (enabledServices != null && !enabledServices.Contains(serviceType)) ||
        disabledServices.Contains(serviceType);
}
