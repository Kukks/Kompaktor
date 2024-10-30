namespace Kompaktor.Utils;

// froom https://stackoverflow.com/a/74573905/275504

public delegate Task AsyncEventHandler<TEventArgs>(object sender, TEventArgs args);