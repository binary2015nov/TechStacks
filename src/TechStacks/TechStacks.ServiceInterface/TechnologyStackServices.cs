﻿using System;
using System.Linq;
using MarkdownSharp;
using ServiceStack;
using ServiceStack.Caching;
using ServiceStack.Configuration;
using ServiceStack.OrmLite;
using TechStacks.ServiceModel;
using TechStacks.ServiceModel.Types;

namespace TechStacks.ServiceInterface
{
    [Authenticate(ApplyTo = ApplyTo.Put | ApplyTo.Post | ApplyTo.Delete)]
    public class TechnologyStackServices : Service
    {
        public MemoryCacheClient MemoryCache { get; set; }

        public object Post(CreateTechnologyStack request)
        {
            var techStack = request.ConvertTo<TechnologyStack>();
            var session = SessionAs<AuthUserSession>();
            techStack.CreatedBy = session.UserName;
            techStack.LastModifiedBy = session.UserName;
            techStack.OwnerId = session.UserAuthId;
            techStack.Created = DateTime.UtcNow;
            techStack.LastModified = DateTime.UtcNow;
            techStack.Slug = techStack.Name.GenerateSlug();
            var id = Db.Insert(techStack, selectIdentity: true);
            var createdTechStack = Db.SingleById<TechnologyStack>(id);

            var history = createdTechStack.ConvertTo<TechnologyStackHistory>();
            history.TechnologyStackId = id;
            history.Operation = "INSERT";
            Db.Insert(history);

            MemoryCache.FlushAll();

            return new CreateTechnologyStackResponse
            {
                Result = createdTechStack.ConvertTo<TechStackDetails>()
            };
        }

        public object Put(UpdateTechnologyStack request)
        {
            var existingStack = Db.SingleById<TechnologyStack>(request.Id);
            if (existingStack == null)
            {
                throw HttpError.NotFound("Tech stack not found");
            }

            var session = SessionAs<AuthUserSession>();
            if (existingStack.IsLocked && !session.HasRole(RoleNames.Admin))
            {
                throw HttpError.Unauthorized("TechnologyStack changes are currently restricted to Administrators only.");
            }

            if (existingStack.OwnerId != session.UserAuthId && !session.HasRole(RoleNames.Admin))
            {
                throw HttpError.Unauthorized("You are not the owner of this stack.");
            }
            var updated = request.ConvertTo<TechnologyStack>();

            //Carry over audit/admin data
            updated.IsLocked = existingStack.IsLocked;
            updated.OwnerId = existingStack.OwnerId;
            updated.CreatedBy = existingStack.CreatedBy;
            updated.LastModifiedBy = session.UserName;
            updated.LastModified = DateTime.UtcNow;

            //Update SlugTitle
            updated.Slug = updated.Name.GenerateSlug();
            Db.Save(updated);

            var history = updated.ConvertTo<TechnologyStackHistory>();
            history.TechnologyStackId = updated.Id;
            history.Operation = "UPDATE";
            Db.Insert(history);

            MemoryCache.FlushAll();

            return new UpdateTechnologyStackResponse
            {
                Result = updated.ConvertTo<TechStackDetails>()
            };
        }

        public object Delete(DeleteTechnologyStack request)
        {
            var stack = Db.SingleById<TechnologyStack>(request.Id);
            if (stack == null)
                throw HttpError.NotFound("Tech stack not found");

            var session = SessionAs<AuthUserSession>();
            if (stack.OwnerId != session.UserAuthId && !session.HasRole(RoleNames.Admin))
                throw HttpError.Unauthorized("You are not the owner of this stack.");

            Db.Delete<TechnologyChoice>(q => q.TechnologyStackId == request.Id);
            Db.DeleteById<TechnologyStack>(request.Id);

            var history = stack.ConvertTo<TechnologyStackHistory>();
            history.TechnologyStackId = stack.Id;
            history.LastModified = DateTime.UtcNow;
            history.LastModifiedBy = session.UserName;
            history.Operation = "DELETE";
            Db.Insert(history);

            MemoryCache.FlushAll();

            return new DeleteTechnologyStackResponse
            {
                Result = new TechnologyStack { Id = request.Id }.ConvertTo<TechStackDetails>()
            }; 
        }

        public object Get(AllTechnologyStacks request)
        {
            return new AllTechnologyStacksResponse
            {
                Results = Db.Select(Db.From<TechnologyStack>().Take(100)).ToList()
            };
        }

        public object Get(TechnologyStacks request)
        {
            var key = "{0}/{1}".Fmt(request.GetType().Name, request.Slug);
            if (request.Reload)
                MemoryCache.Remove(key);

            return base.Request.ToOptimizedResultUsingCache(MemoryCache, key, () =>
            {
                int id;
                var technologyStack = int.TryParse(request.Slug, out id)
                    ? Db.SingleById<TechnologyStack>(id)
                    : Db.Single<TechnologyStack>(x => x.Slug == request.Slug.ToLower());

                if (technologyStack == null)
                    HttpError.NotFound("Tech stack not found");

                var response = GetTechnologyStackWithDetails(technologyStack);
                return response;
            });
        }

        public object Get(TechStackByTier request)
        {
            var query = Db.From<TechnologyStack>();
            if (!string.IsNullOrEmpty(request.Tier))
            {
                //Filter by tier
                query.Join<TechnologyChoice>((stack, choice) => stack.Id == choice.TechnologyStackId);
            }

            return new TechStackByTierResponse
            {
                Results = Db.Select(query).GroupBy(x => x.Id).Select(x => x.First()).ToList()
            };
        }

        public object Get(RecentStackWithTechs request)
        {
            var stackQuery = Db.From<TechnologyStack>()
                    .OrderByDescending(x => x.Id).Limit(20);

            var results = TechStackQueries.GetTechstackDetails(Db, stackQuery);
            return new RecentStackWithTechsResponse
            {
                Results = results
            };
        }

        private TechStackResponse GetTechnologyStackWithDetails(TechnologyStack existingTechStack)
        {
            var technologyChoices = Db.LoadSelect(Db.From<TechnologyChoice>()
                .Join<TechnologyChoice, Technology>((tst, t) => t.Id == tst.TechnologyId)
                .Join<TechnologyChoice, TechnologyStack>((tst, ts) => ts.Id == tst.TechnologyStackId)
                .Where(techChoice => techChoice.TechnologyStackId == existingTechStack.Id));
            var techStack = Db.SingleById<TechnologyStack>(existingTechStack.Id);

            var result = techStack.ConvertTo<TechStackDetails>();
            if (!string.IsNullOrEmpty(techStack.Details))
            {
                result.DetailsHtml = new Markdown().Transform(techStack.Details);
            }

            result.PopulateTechTiers(technologyChoices);

            var response = new TechStackResponse
            {
                Created = DateTime.UtcNow,
                Result = result
            };
            return response;
        }

        public object Any(GetConfig request)
        {
            var allTiers = Enum.GetValues(typeof(TechnologyTier)).Map(x =>
                new Option
                {
                    Name = x.ToString(),
                    Title = typeof(TechnologyTier).GetMember(x.ToString())[0].GetDescription(),
                });

            return new GetConfigResponse
            {
                AllTiers = allTiers,
            };
        }

        public object Any(Overview request)
        {
            if (request.Reload)
                MemoryCache.FlushAll();

            return base.Request.ToOptimizedResultUsingCache(MemoryCache, "overview", () =>
            {
                var response = new OverviewResponse
                {
                    Created = DateTime.UtcNow,

                    LatestTechStacks = TechStackQueries.GetTechstackDetails(Db,
                        Db.From<TechnologyStack>().OrderByDescending(x => x.Id).Limit(20)),

                    TopUsers = Db.Select<UserInfo>(
                        @"select u.user_name as UserName, u.default_profile_url as AvatarUrl, COUNT(*) as StacksCount
                            from technology_stack ts
                                 inner join
                                 user_favorite_technology_stack uf on (ts.id = uf.technology_stack_id)
                                 inner join
                                 custom_user_auth u on (uf.user_id::integer = u.id)
                            group by u.user_name, u.default_profile_url
                            having count(*) > 0
                            order by StacksCount desc
                            limit 20"),

                    TopTechnologies = Db.Select<TechnologyInfo>(
                        @"select tc.technology_id as Id, t.name, COUNT(*) as StacksCount 
                            from technology_choice tc
                                 inner join
                                 technology t on (tc.technology_id = t.id)
                            group by tc.technology_id, t.name
                            having COUNT(*) > 0
                            order by StacksCount desc
                            limit 20"),
                };

                return response;
            });
        }

        public IAutoQuery AutoQuery { get; set; }

        //Cached AutoQuery
        public object Any(FindTechStacks request)
        {
            var key = "{0}/{1}".Fmt(request.GetType().Name, Request.QueryString.ToString());

            return base.Request.ToOptimizedResultUsingCache(MemoryCache, key, () =>
            {
                var q = AutoQuery.CreateQuery(request, Request.GetRequestParams());
                return AutoQuery.Execute(request, q);
            });
        }

    }
}
