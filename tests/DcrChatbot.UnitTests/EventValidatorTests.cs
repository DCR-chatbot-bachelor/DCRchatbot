using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.UnitTests;

public sealed class EventValidatorTests
{
    [Fact]
    public void Validate_Fails_WhenEventIsNotEnabled()
    {
        var dcrEvent = new DcrEvent { Id = "a", IsEnabled = false, DataType = "text" };

        var result = EventValidator.Validate(dcrEvent, "noget");

        Assert.False(result.IsValid);
        Assert.Equal(EventValidationError.EventNotEnabled, result.Error);
    }

    [Fact]
    public void Validate_Succeeds_WhenEnabledTextEventHasValue()
    {
        var dcrEvent = new DcrEvent { Id = "a", IsEnabled = true, DataType = "text" };

        var result = EventValidator.Validate(dcrEvent, "Aarhus");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_Fails_WhenLabelReceivesValue()
    {
        var dcrEvent = new DcrEvent { Id = "A7", IsEnabled = true, DataType = "label" };

        var result = EventValidator.Validate(dcrEvent, "minsu");

        Assert.False(result.IsValid);
        Assert.Equal(EventValidationError.LabelCannotReceiveValue, result.Error);
    }

    [Fact]
    public void Validate_Succeeds_WhenLabelHasNoValue()
    {
        var dcrEvent = new DcrEvent { Id = "A7", IsEnabled = true, DataType = "label" };

        var result = EventValidator.Validate(dcrEvent, null);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_Succeeds_WhenChoiceValueIsAllowed()
    {
        var dcrEvent = new DcrEvent
        {
            Id = "førstegang",
            IsEnabled = true,
            DataType = "choice",
            ChoiceValues = "ja (ja), nej (nej)"
        };

        var result = EventValidator.Validate(dcrEvent, "ja");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_Fails_WhenChoiceValueIsNotAllowed()
    {
        var dcrEvent = new DcrEvent
        {
            Id = "førstegang",
            IsEnabled = true,
            DataType = "choice",
            ChoiceValues = "ja (ja), nej (nej)"
        };

        var result = EventValidator.Validate(dcrEvent, "måske");

        Assert.False(result.IsValid);
        Assert.Equal(EventValidationError.ValueTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_Succeeds_WhenIntegerValueIsNumeric()
    {
        var dcrEvent = new DcrEvent { Id = "alder", IsEnabled = true, DataType = "integer" };

        Assert.True(EventValidator.Validate(dcrEvent, "25").IsValid);
    }

    [Fact]
    public void Validate_Fails_WhenIntegerValueIsNotNumeric()
    {
        var dcrEvent = new DcrEvent { Id = "alder", IsEnabled = true, DataType = "integer" };

        Assert.False(EventValidator.Validate(dcrEvent, "femogtyve").IsValid);
    }

    [Fact]
    public void Validate_Succeeds_WhenDateValueIsValid()
    {
        var dcrEvent = new DcrEvent { Id = "dato", IsEnabled = true, DataType = "date" };

        Assert.True(EventValidator.Validate(dcrEvent, "2026-03-01").IsValid);
    }

    [Fact]
    public void Validate_Fails_WhenDateValueIsInvalid()
    {
        var dcrEvent = new DcrEvent { Id = "dato", IsEnabled = true, DataType = "date" };

        Assert.False(EventValidator.Validate(dcrEvent, "ikke en dato").IsValid);
    }
}