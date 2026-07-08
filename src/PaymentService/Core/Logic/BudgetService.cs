public interface IBudgetService
{
    Task<bool> ReserveBudgetAsync(string category, decimal amount);
    Task ReleaseBudgetAsync(string category, decimal amount);
}

public class BudgetService : IBudgetService
{
    public Task<bool> ReserveBudgetAsync(string category, decimal amount)
    {
        return Task.FromResult(true);
    }

    public Task ReleaseBudgetAsync(string category, decimal amount)
    {
        return Task.CompletedTask;
    }
}
