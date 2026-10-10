using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.Model
{
    public record CreateModelActionDto
    {
        public Guid ProviderId { get; init; }

        public string Name { get; init; } = null!;

        public string DeploymentName { get; init; } = null!;

        public string Description { get; init; } = null!;

        public decimal ContextWindowSize { get; init; }

        public decimal MaxOutputTokens { get; init; }

        public bool IsToolEnabled { get; init; }

        public bool IsReasoningEnabled { get; init; }

        /// <summary>
        /// Whether the deployment is offered in the chat model picker.
        /// </summary>
        /// <remarks>
        /// Initialized to <see langword="true"/>, unlike its sibling flags: the rest of this
        /// request is a full representation where an omitted field takes the CLR default, and the
        /// CLR default here would hide a model nobody asked to hide. An absent property leaves the
        /// initializer alone, so a client written before this field existed keeps its models visible.
        /// <para>
        /// The cost of that choice, stated plainly: a request that omits the property does not
        /// preserve a hidden model's state, it reveals it. A partial payload against the summarizer
        /// puts it back in every user's picker. That is the safer default for the catalog at large,
        /// and it is why <c>ModelService</c> guards the summarizer row specifically.
        /// </para>
        /// </remarks>
        public bool IsUserSelectable { get; init; } = true;

        public bool IsDefault { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 input tokens in USD. Omit it to leave the
        /// model unpriced, which is not the same as free.
        /// </summary>
        public decimal? InputPricePerMillionTokens { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 output tokens in USD, on the same terms as
        /// <see cref="InputPricePerMillionTokens"/>.
        /// </summary>
        public decimal? OutputPricePerMillionTokens { get; init; }
    }

    public class CreateModelActionDtoValidator : AbstractValidator<CreateModelActionDto>
    {
        public CreateModelActionDtoValidator()
        {
            RuleFor(x => x.ProviderId)
                .NotEmpty();
            RuleFor(x => x.Name)
                .NotEmpty()
                .MaximumLength(256);
            RuleFor(x => x.DeploymentName)
                .NotEmpty()
                .MaximumLength(512);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(1024);
            RuleFor(x => x.ContextWindowSize)
                .GreaterThan(0);
            RuleFor(x => x.MaxOutputTokens)
                .GreaterThan(0);
            // The default is the model a turn starts on, and the picker's own route hides a
            // non-selectable row — so a hidden default resolves to nothing on every client and
            // leaves the composer unable to send at all. Refused rather than silently corrected,
            // because the request states two intentions and only the caller knows which to keep.
            // DeactivateModelAsync enforces the same invariant from the other side by clearing
            // IsDefault on the row it retires.
            RuleFor(x => x.IsDefault)
                .Equal(false)
                .When(x => !x.IsUserSelectable)
                .WithMessage("A model hidden from the model picker cannot be the default model.");
            // Zero is accepted where the sibling rules demand a positive value: a genuinely free
            // deployment exists, and omitting the price is how "unpriced" is expressed instead.
            RuleFor(x => x.InputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.InputPricePerMillionTokens.HasValue);
            RuleFor(x => x.OutputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.OutputPricePerMillionTokens.HasValue);
        }
    }

    public record UpdateModelActionDto
    {
        public Guid ProviderId { get; init; }

        public string Name { get; init; } = null!;

        public string DeploymentName { get; init; } = null!;

        public string Description { get; init; } = null!;

        public decimal ContextWindowSize { get; init; }

        public decimal MaxOutputTokens { get; init; }

        public bool IsToolEnabled { get; init; }

        public bool IsReasoningEnabled { get; init; }

        /// <summary>
        /// Whether the deployment is offered in the chat model picker.
        /// </summary>
        /// <remarks>
        /// Initialized to <see langword="true"/>, unlike its sibling flags: the rest of this
        /// request is a full representation where an omitted field takes the CLR default, and the
        /// CLR default here would hide a model nobody asked to hide. An absent property leaves the
        /// initializer alone, so a client written before this field existed keeps its models visible.
        /// <para>
        /// The cost of that choice, stated plainly: a request that omits the property does not
        /// preserve a hidden model's state, it reveals it. A partial payload against the summarizer
        /// puts it back in every user's picker. That is the safer default for the catalog at large,
        /// and it is why <c>ModelService</c> guards the summarizer row specifically.
        /// </para>
        /// </remarks>
        public bool IsUserSelectable { get; init; } = true;

        public bool IsDefault { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 input tokens in USD. Omitting it clears the
        /// stored price, matching the full-representation semantics of the rest of this request.
        /// </summary>
        public decimal? InputPricePerMillionTokens { get; init; }

        /// <summary>
        /// What the deployment charges per 1,000,000 output tokens in USD, on the same terms as
        /// <see cref="InputPricePerMillionTokens"/>.
        /// </summary>
        public decimal? OutputPricePerMillionTokens { get; init; }
    }

    public class UpdateModelActionDtoValidator : AbstractValidator<UpdateModelActionDto>
    {
        public UpdateModelActionDtoValidator()
        {
            RuleFor(x => x.ProviderId)
                .NotEmpty();
            RuleFor(x => x.Name)
                .NotEmpty()
                .MaximumLength(256);
            RuleFor(x => x.DeploymentName)
                .NotEmpty()
                .MaximumLength(512);
            RuleFor(x => x.Description)
                .NotEmpty()
                .MaximumLength(1024);
            RuleFor(x => x.ContextWindowSize)
                .GreaterThan(0);
            RuleFor(x => x.MaxOutputTokens)
                .GreaterThan(0);
            // The default is the model a turn starts on, and the picker's own route hides a
            // non-selectable row — so a hidden default resolves to nothing on every client and
            // leaves the composer unable to send at all. Refused rather than silently corrected,
            // because the request states two intentions and only the caller knows which to keep.
            // DeactivateModelAsync enforces the same invariant from the other side by clearing
            // IsDefault on the row it retires.
            RuleFor(x => x.IsDefault)
                .Equal(false)
                .When(x => !x.IsUserSelectable)
                .WithMessage("A model hidden from the model picker cannot be the default model.");
            // Zero is accepted where the sibling rules demand a positive value: a genuinely free
            // deployment exists, and omitting the price is how "unpriced" is expressed instead.
            RuleFor(x => x.InputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.InputPricePerMillionTokens.HasValue);
            RuleFor(x => x.OutputPricePerMillionTokens)
                .GreaterThanOrEqualTo(0)
                .When(x => x.OutputPricePerMillionTokens.HasValue);
        }
    }
}
