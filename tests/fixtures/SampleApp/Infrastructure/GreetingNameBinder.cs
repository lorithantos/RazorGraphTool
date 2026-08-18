namespace SampleApp.Infrastructure;

using Microsoft.AspNetCore.Mvc.ModelBinding;

// The explicit null where string[] is expected is deliberate: a null array
// argument still reports TypedConstantKind.Array, and this usage keeps the
// extractor honest about it.
[RegisterService<GreetingNameBinder>(null)]
public sealed class GreetingNameBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext) => Task.CompletedTask;
}
