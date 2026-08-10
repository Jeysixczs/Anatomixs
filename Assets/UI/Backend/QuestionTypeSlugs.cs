namespace Anatomia3D.Backend
{
    /// <summary>
    /// String values expected in QuizService.QuestionRecord.QuestionTypeSlug.
    /// Mirrors the Type*/QuestionTypeDisplayToSlug constants in
    /// AdminQuizManagementController - keep both in sync if either changes.
    /// </summary>
    public static class QuestionTypeSlugs
    {
        public const string MultipleChoice = "multiple-choice";
        public const string TrueFalse = "true-false";
        public const string Identification = "identification";
        public const string Enumeration = "enumeration";
        public const string MultipleIdentification = "multiple-identification";

        // Handled by a separate flow per Jeysi - gameplay screen skips this type
        // entirely (see StudentQuizGameplayController's BuildAnswerUi switch).
        public const string ImageBased = "image-based";
    }
}
