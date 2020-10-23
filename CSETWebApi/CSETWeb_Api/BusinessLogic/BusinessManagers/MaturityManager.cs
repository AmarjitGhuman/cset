﻿//////////////////////////////// 
// 
//   Copyright 2020 Battelle Energy Alliance, LLC  
// 
// 
//////////////////////////////// 
using System;
using System.Collections.Generic;
using System.Linq;
using CSETWeb_Api.BusinessLogic.Models;
using DataLayerCore.Model;
using BusinessLogic.Helpers;
using CSETWeb_Api.BusinessLogic.Helpers;
using CSETWeb_Api.BusinessLogic.BusinessManagers.Analysis;
using Microsoft.EntityFrameworkCore;
using CSETWeb_Api.BusinessManagers;
using CSETWeb_Api.Models;
using Microsoft.EntityFrameworkCore.Update;
using Nelibur.ObjectMapper;
using Newtonsoft.Json;


namespace CSETWeb_Api.BusinessLogic.BusinessManagers
{
    public class MaturityManager
    {
        public MaturityManager()
        { }


        /// <summary>
        /// Returns a list of all maturity models selected for the assessment.
        /// </summary>
        /// <param name="assessmentId"></param>
        public List<MaturityModel> GetMaturityModels(int assessmentId)
        {
            using (var db = new CSET_Context())
            {
                var myModels = from amm in db.AVAILABLE_MATURITY_MODELS
                               from mm in db.MATURITY_MODELS
                               where amm.model_id == mm.Maturity_Model_Id && amm.Assessment_Id == assessmentId
                               select new MaturityModel() { ModelId = mm.Maturity_Model_Id, ModelName = mm.Model_Name };
                return myModels.ToList();
            }
        }


        /// <summary>
        /// Saves the selected maturity model.
        /// </summary>
        /// <param name="selectedMaturityModels"></param>
        /// <returns></returns>
        public void PersistSelectedMaturityModel(int assessmentId, string selectedModelName)
        {
            using (var db = new CSET_Context())
            {
                var result = db.AVAILABLE_MATURITY_MODELS.Where(x => x.Assessment_Id == assessmentId);
                db.AVAILABLE_MATURITY_MODELS.RemoveRange(result);

                var mm = db.MATURITY_MODELS.Where(x => x.Model_Name == selectedModelName).FirstOrDefault();
                if (mm != null)
                {
                    db.AVAILABLE_MATURITY_MODELS.Add(new AVAILABLE_MATURITY_MODELS()
                    {
                        Assessment_Id = assessmentId,
                        model_id = mm.Maturity_Model_Id,
                        Selected = true
                    });

                    db.SaveChanges();
                }
            }

            AssessmentUtil.TouchAssessment(assessmentId);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="assessmentId"></param>
        /// <returns></returns>
        public int GetMaturityLevel(int assessmentId)
        {
            using (var db = new CSET_Context())
            {
                var result = db.ASSESSMENT_SELECTED_LEVELS.Where(x => x.Assessment_Id == assessmentId).FirstOrDefault();
                if (result != null)
                {
                    if (int.TryParse(result.Standard_Specific_Sal_Level, out int level))
                    {
                        return level;
                    }
                }

                return 0;
            }
        }


        /// <summary>
        /// Connects the assessment to a Maturity_Level.
        /// </summary>
        public void PersistMaturityLevel(int assessmentId, int level)
        {
            using (var db = new CSET_Context())
            {
                // SAL selections live in ASSESSMENT_SELECTED_LEVELS, which
                // is more complex to allow for the different types of SALs
                // as well as the user's selection(s).

                var result = db.ASSESSMENT_SELECTED_LEVELS.Where(x => x.Assessment_Id == assessmentId && x.Level_Name == "Maturity_Level");
                if (result.Any())
                {
                    db.ASSESSMENT_SELECTED_LEVELS.RemoveRange(result);
                    db.SaveChanges();
                }

                db.ASSESSMENT_SELECTED_LEVELS.Add(new ASSESSMENT_SELECTED_LEVELS()
                {
                    Assessment_Id = assessmentId,
                    Level_Name = "Maturity_Level",
                    Standard_Specific_Sal_Level = level.ToString()
                });

                db.SaveChanges();
            }

            AssessmentUtil.TouchAssessment(assessmentId);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="assessmentId"></param>
        public object GetMaturityQuestions(int assessmentId)
        {
            // Populate response
            var response = new QuestionResponse
            {
                Domains = new List<Domain>()
            };


            using (var db = new CSET_Context())
            {
                var myModel = db.AVAILABLE_MATURITY_MODELS
                    .Include(x => x.model_)
                    .Where(x => x.Assessment_Id == assessmentId).FirstOrDefault();

                if (myModel == null)
                {
                    return response;
                }

                // see if any answer options should not be in the list
                var suppressedAnswerOptions =  myModel.model_.Answer_Options_Suppressed;
                if (!string.IsNullOrEmpty(suppressedAnswerOptions))
                {
                    var a = suppressedAnswerOptions.Split(',');
                    foreach (string suppress in a)
                    {
                        response.AnswerOptions.Remove(suppress);
                    }
                }


                // The maturity target level is stored similar to a SAL level
                int targetLevel = 0;
                var myLevel = db.ASSESSMENT_SELECTED_LEVELS.Where(x => x.Assessment_Id == assessmentId && x.Level_Name == "Maturity_Level").FirstOrDefault();
                if (myLevel != null)
                {
                    targetLevel = int.Parse(myLevel.Standard_Specific_Sal_Level);
                }

                // get the level display names
                // (for now assume that the assessment uses a single model)
                var levelNames = new List<MaturityLevel>();
                foreach (var l in db.MATURITY_LEVELS.Where(x => x.Maturity_Model_Id == myModel.model_id)
                    .OrderBy(y => y.Level).ToList())
                {
                    levelNames.Add(new MaturityLevel()
                    {
                        Level = l.Level,
                        Label = l.Level_Name,
                        Applicable = l.Level <= targetLevel
                    });
                }


                // Get all maturity questions for the model regardless of level
                // The user can choose to see questions above the target level via filtering. 
                var questions = db.MATURITY_QUESTIONS.Where(q =>
                    myModel.model_id == q.Maturity_Model_Id).ToList();


                // Get all MATURITY answers for the assessment
                var answers = from a in db.ANSWER.Where(x => x.Assessment_Id == assessmentId && x.Is_Maturity)
                              from b in db.VIEW_QUESTIONS_STATUS.Where(x => x.Answer_Id == a.Answer_Id).DefaultIfEmpty()
                              select new FullAnswer() { a = a, b = b };




                // CMMC has 17 domains, which correspond to Categories in the 
                // MATURITY_QUESTIONS table.
                // TODO:  Eventually they should probably be defined in a new generic
                // MATURITY_DOMAINS table.
                var domains = questions.Select(x => x.Category).Distinct().ToList();

                // build a container for each domain
                foreach (var d in domains)
                {
                    response.Domains.Add(new Domain()
                    {
                        DisplayText = d,
                        DomainText = d,
                        Levels = levelNames
                    });
                }

                foreach (var dbR in questions)
                {
                    // Make sure there are no leading or trailing spaces - it will affect the tree structure that is built
                    dbR.Category = dbR.Category ?? dbR.Category.Trim();
                    dbR.Sub_Category = dbR.Sub_Category ?? dbR.Sub_Category.Trim();

                    // If the Standard_Sub_Category is null (like CSC_V6), default it to the Standard_Category
                    if (dbR.Sub_Category == null)
                    {
                        dbR.Sub_Category = dbR.Category;
                    }


                    var json = JsonConvert.SerializeObject(response);

                    // drop into the domain
                    var targetDomain = response.Domains.Where(cc => cc.DomainText == dbR.Category).FirstOrDefault();
                    if (targetDomain != null)
                    {
                        // find or create a Category
                        var targetCat = targetDomain.Categories.Where(c => c.GroupHeadingText == dbR.Category).FirstOrDefault();
                        if (targetCat == null)
                        {
                            targetCat = new QuestionGroup()
                            {
                                GroupHeadingText = dbR.Category
                            };
                            targetDomain.Categories.Add(targetCat);
                        }


                        // find or create a Subcategory
                        var targetSubcat = targetCat.SubCategories.Where(sc => sc.SubCategoryHeadingText == dbR.Sub_Category).FirstOrDefault();
                        if (targetSubcat == null)
                        {
                            targetSubcat = new QuestionSubCategory()
                            {
                                SubCategoryId = 0,
                                SubCategoryHeadingText = dbR.Sub_Category,
                                // GroupHeadingId = g.GroupHeadingId
                            };

                            targetCat.SubCategories.Add(targetSubcat);
                        }




                        FullAnswer answer = answers.Where(x => x.a.Question_Or_Requirement_Id == dbR.Mat_Question_Id).FirstOrDefault();

                        var qa = new QuestionAnswer()
                        {
                            DisplayNumber = dbR.Question_Title,
                            QuestionId = dbR.Mat_Question_Id,
                            QuestionText = dbR.Question_Text.Replace("\r\n", "<br/>").Replace("\n", "<br/>").Replace("\r", "<br/>"),
                            Answer = answer?.a.Answer_Text,
                            AltAnswerText = answer?.a.Alternate_Justification,
                            Comment = answer?.a.Comment,
                            Feedback = answer?.a.Feedback,
                            MarkForReview = answer?.a.Mark_For_Review ?? false,
                            Reviewed = answer?.a.Reviewed ?? false,
                            MaturityLevel = dbR.Maturity_Level,
                            SetName = string.Empty,
                            Is_Maturity = answer?.a.Is_Maturity ?? true,
                            Is_Component = answer?.a.Is_Component ?? false,
                            Is_Requirement = answer?.a.Is_Requirement ?? false
                        };
                        if (answer != null)
                        {
                            TinyMapper.Bind<VIEW_QUESTIONS_STATUS, QuestionAnswer>();
                            TinyMapper.Map(answer.b, qa);
                        }

                        qa.ParmSubs = null;

                        targetSubcat.Questions.Add(qa);
                    }
                }

                return response;
            }
        }


        /// <summary>
        /// Stores an answer.
        /// </summary>
        /// <param name="answer"></param>
        public int StoreAnswer(int assessmentId, Answer answer)
        {
            var db = new CSET_Context();

            // Find the Maturity Question
            var question = db.MATURITY_QUESTIONS.Where(q => q.Mat_Question_Id == answer.QuestionId).FirstOrDefault();

            if (question == null)
            {
                throw new Exception("Unknown question or requirement ID: " + answer.QuestionId);
            }


            // in case a null is passed, store 'unanswered'
            if (string.IsNullOrEmpty(answer.AnswerText))
            {
                answer.AnswerText = "U";
            }

            ANSWER dbAnswer = db.ANSWER.Where(x => x.Assessment_Id == assessmentId
                && x.Question_Or_Requirement_Id == answer.QuestionId
                && x.Is_Requirement == answer.Is_Requirement).FirstOrDefault();


            if (dbAnswer == null)
            {
                dbAnswer = new ANSWER();
            }

            dbAnswer.Assessment_Id = assessmentId;
            dbAnswer.Question_Or_Requirement_Id = answer.QuestionId;
            dbAnswer.Question_Number = answer.QuestionNumber;
            dbAnswer.Answer_Text = answer.AnswerText;
            dbAnswer.Alternate_Justification = answer.AltAnswerText;
            dbAnswer.Comment = answer.Comment;
            dbAnswer.Feedback = answer.Feedback;
            dbAnswer.Mark_For_Review = answer.MarkForReview;
            dbAnswer.Reviewed = answer.Reviewed;
            dbAnswer.Is_Maturity = answer.Is_Maturity;
            dbAnswer.Is_Component = answer.Is_Component;
            dbAnswer.Component_Guid = answer.ComponentGuid;
            dbAnswer.Is_Requirement = answer.Is_Requirement;

            db.ANSWER.AddOrUpdate(dbAnswer, x => x.Answer_Id);
            db.SaveChanges();

            AssessmentUtil.TouchAssessment(assessmentId);

            return dbAnswer.Answer_Id;
        }


        // The methods that follow were originally built for NCUA/ACET.
        // It is hoped that they will eventually be refactored to fit a more
        // 'generic' approach to maturity models.




        public List<MaturityDomain> GetMaturityAnswers(int assessmentId)
        {
            using (var db = new CSET_Context())
            {
                var data = db.usp_MaturityDetailsCalculations(assessmentId).ToList();
                return CalculateComponentValues(data, assessmentId);
            }

        }

        public bool GetTargetBandOnly(int assessmentId)
        {
            using (var db = new CSET_Context())
            {
                bool? defaultTarget = db.ASSESSMENTS.Where(x => x.Assessment_Id == assessmentId).FirstOrDefault().MatDetail_targetBandOnly;
                return defaultTarget ?? false;
            }
        }

        public void SetTargetBandOnly(int assessmentId, bool value)
        {
            using (var db = new CSET_Context())
            {
                var assessment = db.ASSESSMENTS.Where(x => x.Assessment_Id == assessmentId).FirstOrDefault();
                assessment.MatDetail_targetBandOnly = value;
                db.SaveChanges();
            }
        }


        /// <summary>
        /// Calculate maturity levels of components
        /// </summary>
        /// <param name="maturity"></param>
        /// <returns></returns>
        public List<MaturityDomain> CalculateComponentValues(List<usp_MaturityDetailsCalculations_Result> maturity, int assessmentId)
        {
            using (var db = new CSET_Context())
            {
                var maturityDomains = new List<MaturityDomain>();
                var domains = db.FINANCIAL_DOMAINS.ToList();
                var standardCategories = db.FINANCIAL_DETAILS.ToList();
                var sub_categories = from m in maturity
                                     group new { m.Domain, m.AssessmentFactor, m.FinComponent }
                                      by new { m.Domain, m.AssessmentFactor, m.FinComponent } into mk
                                     select new
                                     {
                                         mk.Key.Domain,
                                         mk.Key.AssessmentFactor,
                                         mk.Key.FinComponent
                                     };

                //var maturityRange = GetMaturityRange(assessmentId);

                if (maturity.Count > 0)
                {
                    foreach (var d in domains)
                    {
                        var tGroupOrder = maturity.FirstOrDefault(x => x.Domain == d.Domain);
                        var maturityDomain = new MaturityDomain
                        {
                            DomainName = d.Domain,
                            Assessments = new List<MaturityAssessment>(),
                            Sequence = tGroupOrder == null ? 0 : tGroupOrder.grouporder
                        };
                        var partial_sub_categoy = sub_categories.Where(x => x.Domain == d.Domain).GroupBy(x => x.AssessmentFactor).Select(x => x.Key);
                        foreach (var s in partial_sub_categoy)
                        {


                            var maturityAssessment = new MaturityAssessment
                            {
                                AssessmentFactor = s,
                                Components = new List<MaturityComponent>(),
                                Sequence = (int)maturity.FirstOrDefault(x => x.AssessmentFactor == s).grouporder

                            };
                            var assessmentCategories = sub_categories.Where(x => x.AssessmentFactor == s);
                            foreach (var c in assessmentCategories)
                            {

                                var component = new MaturityComponent
                                {
                                    ComponentName = c.FinComponent,
                                    Sequence = (int)maturity.FirstOrDefault(x => x.FinComponent == c.FinComponent).grouporder

                                };
                                var baseline = new SalAnswers
                                {
                                    UnAnswered = !maturity.FirstOrDefault(x => x.FinComponent == c.FinComponent).Complete,
                                    Answered = maturity.Any(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.BaselineMaturity.ToUpper()) ? Convert.ToInt32(maturity.FirstOrDefault(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.BaselineMaturity.ToUpper()).AnswerPercent * 100) : 0

                                };
                                var evolving = new SalAnswers
                                {

                                    Answered = maturity.Any(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.EvolvinMaturity.ToUpper()) ? Convert.ToInt32(maturity.FirstOrDefault(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.EvolvinMaturity.ToUpper()).AnswerPercent * 100) : 0


                                };
                                var intermediate = new SalAnswers
                                {

                                    Answered = maturity.Any(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.IntermediateMaturity.ToUpper()) ? Convert.ToInt32(maturity.FirstOrDefault(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.IntermediateMaturity.ToUpper()).AnswerPercent * 100) : 0

                                };
                                var advanced = new SalAnswers
                                {

                                    Answered = maturity.Any(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.AdvancedMaturity.ToUpper()) ? Convert.ToInt32(maturity.FirstOrDefault(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.AdvancedMaturity.ToUpper()).AnswerPercent * 100) : 0

                                };
                                var innovative = new SalAnswers
                                {

                                    Answered = maturity.Any(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.InnovativeMaturity.ToUpper()) ? Convert.ToInt32(maturity.FirstOrDefault(x => x.FinComponent == c.FinComponent && x.MaturityLevel == Constants.InnovativeMaturity.ToUpper()).AnswerPercent * 100) : 0

                                };
                                component.Baseline = baseline.Answered;
                                component.Evolving = evolving.Answered;
                                component.Intermediate = intermediate.Answered;
                                component.Advanced = advanced.Answered;
                                component.Innovative = innovative.Answered;
                                component.AssessedMaturityLevel = baseline.UnAnswered ? Constants.IncompleteMaturity :
                                                                    baseline.Answered < 100 ? Constants.SubBaselineMaturity :
                                                                        evolving.Answered < 100 ? Constants.BaselineMaturity :
                                                                            intermediate.Answered < 100 ? Constants.EvolvinMaturity :
                                                                                advanced.Answered < 100 ? Constants.IntermediateMaturity :
                                                                                    innovative.Answered < 100 ? Constants.AdvancedMaturity :
                                                                                    "Innovative";

                                maturityAssessment.Components.Add(component);

                            }

                            maturityAssessment.AssessmentFactorMaturity = maturityAssessment.Components.Any(x => x.AssessedMaturityLevel == Constants.IncompleteMaturity) ? Constants.IncompleteMaturity :
                                                                           maturityAssessment.Components.Any(x => x.AssessedMaturityLevel == Constants.SubBaselineMaturity) ? Constants.SubBaselineMaturity :
                                                                           maturityAssessment.Components.Any(x => x.AssessedMaturityLevel == Constants.BaselineMaturity) ? Constants.BaselineMaturity :
                                                                               maturityAssessment.Components.Any(x => x.AssessedMaturityLevel == Constants.EvolvinMaturity) ? Constants.EvolvinMaturity :
                                                                                maturityAssessment.Components.Any(x => x.AssessedMaturityLevel == Constants.IntermediateMaturity) ? Constants.IntermediateMaturity :
                                                                                   maturityAssessment.Components.Any(x => x.AssessedMaturityLevel == Constants.AdvancedMaturity) ? Constants.AdvancedMaturity :
                                                                                       maturityAssessment.Components.Any(x => x.AssessedMaturityLevel == Constants.InnovativeMaturity) ? Constants.InnovativeMaturity :
                                                                                           Constants.IncompleteMaturity;
                            maturityAssessment.Components = maturityAssessment.Components.OrderBy(x => x.Sequence).ToList();
                            maturityDomain.Assessments.Add(maturityAssessment);



                        }

                        maturityDomain.DomainMaturity = maturityDomain.Assessments.Any(x => x.AssessmentFactorMaturity == Constants.IncompleteMaturity) ? Constants.IncompleteMaturity :
                                                                            maturityDomain.Assessments.Any(x => x.AssessmentFactorMaturity == Constants.SubBaselineMaturity) ? Constants.SubBaselineMaturity :
                                                                               maturityDomain.Assessments.Any(x => x.AssessmentFactorMaturity == Constants.BaselineMaturity) ? Constants.BaselineMaturity :
                                                                                   maturityDomain.Assessments.Any(x => x.AssessmentFactorMaturity == Constants.EvolvinMaturity) ? Constants.EvolvinMaturity :
                                                                                       maturityDomain.Assessments.Any(x => x.AssessmentFactorMaturity == Constants.IntermediateMaturity) ? Constants.IntermediateMaturity :
                                                                                        maturityDomain.Assessments.Any(x => x.AssessmentFactorMaturity == Constants.AdvancedMaturity) ? Constants.AdvancedMaturity :
                                                                                            maturityDomain.Assessments.Any(x => x.AssessmentFactorMaturity == Constants.InnovativeMaturity) ? Constants.InnovativeMaturity :
                                                                                                Constants.IncompleteMaturity;
                        maturityDomain.Assessments = maturityDomain.Assessments.OrderBy(x => x.Sequence).ToList();
                        maturityDomains.Add(maturityDomain);
                    }
                }
                maturityDomains = maturityDomains.OrderBy(x => x.Sequence).ToList();
                return maturityDomains;
            }
        }

        /// <summary>
        /// Get matrix for maturity determination based on total irp rating
        /// </summary>
        /// <param name="irpRating"></param>
        /// <returns></returns>
        public List<string> GetMaturityRange(int assessmentId)
        {
            ACETDashboardManager manager = new ACETDashboardManager();
            ACETDashboard irpCalculation = manager.GetIrpCalculation(assessmentId);
            bool targetBandOnly = GetTargetBandOnly(assessmentId);
            int irpRating = irpCalculation.Override > 0 ? irpCalculation.Override : irpCalculation.SumRiskLevel;
            if (!targetBandOnly)
                irpRating = 6; //Do the default configuration
            return irpSwitch(irpRating);
        }

        public List<string> irpSwitch(int irpRating)
        {
            switch (irpRating)
            {
                case 1:
                    return new List<string> { Constants.BaselineMaturity, Constants.EvolvinMaturity };
                case 2:
                    return new List<string>
                        {Constants.BaselineMaturity, Constants.EvolvinMaturity, Constants.IntermediateMaturity};
                case 3:
                    return new List<string>
                        {Constants.EvolvinMaturity, Constants.IntermediateMaturity, Constants.AdvancedMaturity};
                case 4:
                    return new List<string>
                        {Constants.IntermediateMaturity, Constants.AdvancedMaturity, Constants.InnovativeMaturity};
                case 5:
                    return new List<string> { Constants.AdvancedMaturity, Constants.InnovativeMaturity };
                default:
                    return new List<string>
                    {
                        Constants.BaselineMaturity, Constants.EvolvinMaturity, Constants.IntermediateMaturity,
                        Constants.AdvancedMaturity, Constants.InnovativeMaturity
                    };
            }
        }

        /// <summary>
        /// Returns a Dictionary mapping requirement ID to its corresponding maturity level.
        /// </summary>
        /// <returns></returns>
        public Dictionary<int, MaturityMap> GetRequirementMaturityLevels()
        {
            using (var db = new CSET_Context())
            {
                var q = from req in db.NEW_REQUIREMENT
                        join fr in db.FINANCIAL_REQUIREMENTS on req.Requirement_Id equals fr.Requirement_Id
                        join fd in db.FINANCIAL_DETAILS on fr.StmtNumber equals fd.StmtNumber
                        join fg in db.FINANCIAL_GROUPS on fd.FinancialGroupId equals fg.FinancialGroupId
                        join fm in db.FINANCIAL_MATURITY on fg.MaturityId equals fm.MaturityId
                        where req.Original_Set_Name == "ACET_V1"
                        select new { req.Requirement_Id, fr.StmtNumber, fm.MaturityId, fm.Acronym, fm.MaturityLevel };

                var dict = new Dictionary<int, MaturityMap>();
                foreach (var a in q)
                {
                    dict.Add(a.Requirement_Id, new MaturityMap(a.MaturityId, a.Acronym, a.MaturityLevel));
                }

                return dict;
            }
        }
    }
}
