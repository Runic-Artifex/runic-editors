namespace Demo;

public sealed partial class CounterViewModel
{
    public int Count { get; private set; }

    public void Increment()
    {
        Count++;
    }
}
