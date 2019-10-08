﻿namespace DataLayerCore.Model
{
    public partial class usp_GetQuestionsWithFeedback
    {
        public string Feedback { get; set; }
        public string QuestionText { get; set; }
        public int QuestionOrRequirementId { get; set; }
        public int AnswerId { get; set; }
        public string SetName { get; set; }
    }

    public partial class FullFeedbackText
    {
        public string FeedbackText { get; set; }
    }
}