using ContableAI.Application.Features.Companies.Commands;
using FluentValidation;
using System.Text.RegularExpressions;

namespace ContableAI.Application.Validators;

public class CreateCompanyCommandValidator : AbstractValidator<CreateCompanyCommand>
{
    // Valid CUIT formats: XX-XXXXXXXX-X (with or without hyphens)
    private static readonly Regex CuitRegex = new(@"^\d{2}-?\d{8}-?\d$", RegexOptions.Compiled);

    public CreateCompanyCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Company name is required.")
            .MaximumLength(200).WithMessage("Company name must not exceed 200 characters.");

        RuleFor(x => x.Cuit)
            .NotEmpty().WithMessage("CUIT is required.")
            .Must(cuit => CuitRegex.IsMatch(cuit ?? ""))
            .WithMessage("CUIT must match the format XX-XXXXXXXX-X (e.g. 30-12345678-9).");

        RuleFor(x => x.BusinessType)
            .MaximumLength(100)
            .When(x => x.BusinessType is not null);

        RuleFor(x => x.BankAccountName)
            .MaximumLength(200)
            .When(x => x.BankAccountName is not null);
    }
}

public class UpdateCompanyCommandValidator : AbstractValidator<UpdateCompanyCommand>
{
    public UpdateCompanyCommandValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithMessage("Company ID is required.");

        RuleFor(x => x.Name)
            .MaximumLength(200)
            .When(x => x.Name is not null);

        RuleFor(x => x.BusinessType)
            .MaximumLength(100)
            .When(x => x.BusinessType is not null);
    }
}

public class CreateCompanyRuleCommandValidator : AbstractValidator<CreateCompanyRuleCommand>
{
    private static readonly string[] ValidDirections = ["DEBIT", "CREDIT"];

    public CreateCompanyRuleCommandValidator()
    {
        RuleFor(x => x.CompanyId)
            .NotEmpty().WithMessage("Company ID is required.");

        RuleFor(x => x.Keyword)
            .NotEmpty().WithMessage("Keyword is required.")
            .MaximumLength(200);

        RuleFor(x => x.TargetAccount)
            .NotEmpty().WithMessage("Target account is required.")
            .MaximumLength(200);

        RuleFor(x => x.Direction)
            .Must(d => d is null || ValidDirections.Contains(d.ToUpper()))
            .WithMessage("Direction must be 'DEBIT', 'CREDIT', or null.")
            .When(x => x.Direction is not null);

        RuleFor(x => x.Priority)
            .InclusiveBetween(1, 999)
            .When(x => x.Priority.HasValue)
            .WithMessage("Priority must be between 1 and 999.");
    }
}
