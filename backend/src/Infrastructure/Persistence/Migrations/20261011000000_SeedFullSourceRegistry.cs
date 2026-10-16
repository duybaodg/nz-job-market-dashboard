using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Seeds every site from the NZ IT job website list into <c>job_sources</c>.
/// Generated from crawler/sources/sites.json by
/// crawler/tools/generate_source_migration.py — edit the registry, not this file.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261011000000_SeedFullSourceRegistry")]
public sealed class SeedFullSourceRegistry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        -- Reconcile legacy source names with their registry names first.
        DELETE FROM job_sources
        WHERE name = 'Salt'
          AND EXISTS (SELECT 1 FROM job_sources existing WHERE existing.name = 'Salt NZ');
        UPDATE jobs SET source = 'Salt NZ' WHERE source = 'Salt';
        UPDATE job_sources SET name = 'Salt NZ', updated_at = NOW()
        WHERE name = 'Salt';
        DELETE FROM job_sources
        WHERE name = 'LinkedIn'
          AND EXISTS (SELECT 1 FROM job_sources existing WHERE existing.name = 'LinkedIn Jobs');
        UPDATE jobs SET source = 'LinkedIn Jobs' WHERE source = 'LinkedIn';
        UPDATE job_sources SET name = 'LinkedIn Jobs', updated_at = NOW()
        WHERE name = 'LinkedIn';

        INSERT INTO job_sources
            (id, name, base_url, method, parser, enabled, crawl_frequency_minutes,
             terms_checked_at, created_at, updated_at)
        VALUES
            ('25ca0abf-4299-53fa-b1f1-447045959d90', 'Absolute IT', 'https://absoluteit.co.nz/it-jobs/', 'Scrapling', 'absolute-it', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('492448b7-d3c5-5135-b885-75a8cfe2aa43', 'Beyond Recruitment', 'https://www.beyondrecruitment.co.nz/jobs/', 'Scrapling', 'beyond-recruitment', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('1a065aa9-02fe-5435-a507-ec758525579c', 'Indeed NZ', 'https://nz.indeed.com/jobs?q=IT&l=New+Zealand', 'Scrapling', 'generic', FALSE, 720,
             NOW(), NOW(), NOW()),
            ('3ce64eac-8d3d-5fe7-9ae3-c3554977751e', 'NZ Government Jobs', 'https://jobs.govt.nz/jobtools/jncustomsearch.searchResults?in_organid=16563&in_jobDate=All&in_multi01=%22IT%20%26%20computing%22&in_multi01_id=1802&in_orderby=dateinput%20desc', 'Scrapling', 'govt-nz', TRUE, 720,
             NOW(), NOW(), NOW()),
            ('3013bc05-aec2-56d5-94d7-2f1b9607b68a', 'Potentia', 'https://www.potentia.co.nz/jobs/', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('525b4d2c-cc30-5528-9a8b-fb72cf6473e4', 'Recruit IT', 'https://www.recruitit.co.nz/jobs/', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('cdf5d946-7522-59fd-8bb6-4b480110839b', 'SEEK NZ', 'https://nz.seek.com/information-technology-jobs', 'Scrapling', 'seek', TRUE, 10080,
             NOW(), NOW(), NOW()),
            ('c9816110-be43-5111-910c-f2e46ed7ad3b', 'Talent Army', 'https://talent.army/job-board', 'Scrapling', 'talent-army', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('b21a2a74-679b-5534-a265-0a86f8100344', 'Trade Me Jobs', 'https://www.trademe.co.nz/a/jobs/it', 'Scrapling', 'trade-me', TRUE, 10080,
             NOW(), NOW(), NOW()),
            ('ea11c8f4-02fe-51c8-961a-db97ca1f5e08', 'Alpha Recruitment', 'https://www.alphajobs.co.nz/job', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('94220e10-b3af-5611-b5fe-5df1f1f1abdf', 'Comspek International', 'https://www.comspek.co.nz/jobs/', 'Scrapling', 'comspek', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('db9362c8-7e4b-5beb-9d52-04ab0d96cda9', 'Consult Recruitment', 'https://www.consult.co.nz/jobs/', 'Scrapling', 'consult-recruitment', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('07a7afc1-7d32-57ed-84ff-ad123bec6254', 'Digital Garage', 'https://digitalgarage.co.nz/jobs/', 'Scrapling', 'digital-garage', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('2f251046-8b79-55c4-8d1f-699be0be18b5', 'Hays NZ', 'https://www.hays.net.nz/job-search?q=technology', 'Scrapling', 'hays', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('07ad6a78-1b4b-5270-bc66-ad7f04767a53', 'JobSeek', 'https://jobseek.nz/Jobs', 'Scrapling', 'jobseek', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('e5740a52-d303-5260-8b4b-82585d8ee4f6', 'Madison Recruitment', 'https://madison.co.nz/jobs/', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('43dcb9ca-b78c-5497-9662-46e3c4f53fb1', 'PlaceMe IT', 'https://www.placeme.net.nz/jobs', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('c0a216f1-1f7c-55c1-9242-7cfe032e1057', 'Randstad NZ', 'https://www.randstad.co.nz/jobs/', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('a5b2f511-5c6c-51dd-b969-c97bc4be554d', 'Robert Walters NZ', 'https://www.robertwalters.co.nz/jobs.html', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('fdd95252-e88e-543a-acf1-91fea6927428', 'Salt NZ', 'https://welovesalt.com/nz/jobs/', 'Scrapling', 'salt', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('b2b02a27-b5b3-5aa2-9a39-4061e2e01edd', 'TEKsystems NZ', 'https://www.teksystems.com/en/careers', 'Scrapling', 'generic', FALSE, 1440,
             NOW(), NOW(), NOW()),
            ('97e9744e-ff77-5cf4-a7bc-f9c347d6ceea', 'Talent International NZ', 'https://www.talentinternational.com/search-jobs/?search=&country=new-zealand', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('1c0c434e-fdb2-57f7-9ee5-84f2af6db6be', 'Talent Vault', 'https://www.talentvault.co.nz/jobs/', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('440c6efc-4942-5acd-8cc6-4afdf11ee57a', 'Tribe Recruitment', 'https://www.tribegroup.com/api/jobs', 'Scrapling', 'tribe', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('9453f8b6-a045-573b-b9fe-0a56e7d9c46f', 'Younity', 'https://younity.co.nz/jobseekers/', 'Scrapling', 'generic', FALSE, 1440,
             NOW(), NOW(), NOW()),
            ('d6174ab0-6602-5e5c-8262-44cb0289ef8f', 'ZEIL', 'https://www.zeil.com/jobs/new-zealand/information-technology', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('276f6f69-0972-5a93-b147-b34afcb3e52e', 'DOC Careers', 'https://www.doc.govt.nz/careers/', 'Scrapling', 'workday', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('e78bc615-920d-52cc-bdff-158ab204876e', 'GradConnection NZ', 'https://nz.gradconnection.com/jobs/', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('11fc0988-32ba-5dbe-acc6-3a05bf06d061', 'Inland Revenue Careers', 'https://ekfu.fa.ap1.oraclecloud.com/hcmRestApi/resources/latest/recruitingCEJobRequisitions?expand=requisitionList&finder=findReqs;siteNumber=CX_1001,limit=200,sortBy=POSTING_DATES_DESC', 'Scrapling', 'oracle', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('be49b6b5-dd94-55ee-9011-017520faaa78', 'MPI Careers', 'https://careers.mpi.govt.nz/jobtools/jncustomsearch.searchResults?in_organid=18152&in_jobDate=All&in_orderby=dateinput%20desc', 'Scrapling', 'govt-nz', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('2228a499-de12-5f70-bc67-61de130d08fe', 'NZ Defence Careers', 'https://defencecareers.my.site.com/civilianportal/webruntime/api/apex/execute?cacheable=true&classname=%40udd%2F01pVR000002mj9t&isContinuation=false&method=getOpenVacancies&namespace=&language=en-NZ&asGuest=true&htmlEncode=false', 'Scrapling', 'salesforce', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('07bae331-4210-55e9-85f4-d3139b348315', 'Prosple NZ', 'https://nz.prosple.com/graduate-jobs', 'Scrapling', 'generic', FALSE, 1440,
             NOW(), NOW(), NOW()),
            ('7abd9d4e-34e0-5968-845d-2f816c99e62e', 'Student Job Search', 'https://www.sjs.co.nz/job-seeker/jobs', 'Scrapling', 'generic', FALSE, 1440,
             NOW(), NOW(), NOW()),
            ('8d32b40a-545e-5ded-b8de-179ab0d31dd0', 'ANZ NZ', 'https://careers.anz.com/search/?q=technology&locationsearch=New+Zealand', 'Scrapling', 'pageup', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('da05185d-d1b8-5613-a57e-e3869ab3cbbd', 'ASB', 'https://www.asb.co.nz/careers', 'Scrapling', 'generic', FALSE, 1440,
             NOW(), NOW(), NOW()),
            ('e8041419-d78f-5012-a090-a1d462730df1', 'Air New Zealand', 'https://jobs.smartrecruiters.com/AirNewZealand', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('4958c358-7cb0-5814-b049-2e219f4b346b', 'BNZ', 'https://nab.eightfold.ai/careers?microsite=bnz', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('5d3f583f-ee16-5ca8-9868-d1d064c8dedd', 'Datacom', 'https://datacom.com/nz/en/careers/search', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('8cbf4c37-622b-5eaa-973e-23e077cbfa35', 'EROAD', 'https://eroadgroup.wd3.myworkdayjobs.com/EROAD', 'Scrapling', 'workday', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('da38630f-6d74-5251-93e3-d84814328b0d', 'Fisher & Paykel', 'https://haier.wd3.myworkdayjobs.com/FPA_External_Career_Site', 'Scrapling', 'workday', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('4f4a34d8-c386-5747-8ed0-f5b37f614b2c', 'Fonterra', 'https://careers.fonterra.com/careers/SearchJobs/feed/', 'Scrapling', 'rss', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('b42e7757-9cb0-5e5e-b7c5-54f35f1cb2b5', 'Gallagher', 'https://careers.gallagher.com/jobs', 'Scrapling', 'jsonld', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('c2079d48-2746-5c51-82bf-cb297577f963', 'Halter', 'https://api.ashbyhq.com/posting-api/job-board/halter', 'Scrapling', 'halter-ashby', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('964a3314-b14c-5a91-8a81-2514d00f35e9', 'Jade Software', 'https://www.jadeworld.com/careers', 'Scrapling', 'bamboohr', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('013e2d78-3a65-5653-a23c-0fe31c2bf538', 'LIC', 'https://careers.lic.co.nz/jobs', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('1141bed2-bf5d-55fa-ba7c-0bb9bc5a0a03', 'LinkedIn Jobs', 'https://www.linkedin.com/jobs/', 'Scrapling', 'linkedin', FALSE, 1440,
             NOW(), NOW(), NOW()),
            ('c4811c2c-d9e2-5d63-832d-9862e8a13b44', 'MyJobSpace', 'https://www.myjobspace.co.nz/jobs', 'Scrapling', 'generic', FALSE, 1440,
             NOW(), NOW(), NOW()),
            ('8c11f5d5-eb23-5ac1-8a72-1091825c4be6', 'NZ Police', 'https://www.newcops.govt.nz/', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('ae204ec8-cd67-5146-9cb9-b58d9f4c5e87', 'One NZ', 'https://careers.one.nz/go/Technology/7847210', 'Scrapling', 'pageup', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('f75bd289-7470-5902-b96a-0d234fe48a74', 'Orion Health', 'https://orionhealth.com/nz/company/careers/', 'Scrapling', 'generic', FALSE, 1440,
             NOW(), NOW(), NOW()),
            ('1c1d3637-2fd9-56a7-a35c-3183c781a44b', 'Pushpay', 'https://boards-api.greenhouse.io/v1/boards/pushpay/jobs?content=true', 'Scrapling', 'greenhouse', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('77e2f764-ab42-5ea7-a3ca-148ad1c2ae44', 'Rocket Lab', 'https://boards-api.greenhouse.io/v1/boards/rocketlab/jobs?content=true', 'Scrapling', 'greenhouse', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('538ff3ac-315e-5004-b3af-815bcf2728a0', 'Ryman Healthcare', 'https://careers.rymanhealthcare.com/', 'Scrapling', 'generic', FALSE, 1440,
             NOW(), NOW(), NOW()),
            ('0fcaac2f-e38a-53d8-9e6e-9d341ae7a8c8', 'Serko', 'https://www.serko.com/careers/', 'Scrapling', 'generic', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('91db3bb3-d8bd-5002-8165-a1052e315215', 'Spark NZ', 'https://careers.sparknz.co.nz/careers/SearchJobs/feed/?jobRecordsPerPage=100', 'Scrapling', 'rss', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('895d34f5-aecf-573a-80b0-e36860a9881e', 'Trimble', 'https://trimble.wd1.myworkdayjobs.com/trimblecareers', 'Scrapling', 'workday', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('97741811-ba2a-5cad-abc5-6814119e52f8', 'Westpac NZ', 'https://westpacnz.wd105.myworkdayjobs.com/Westpac_Careers', 'Scrapling', 'workday', TRUE, 1440,
             NOW(), NOW(), NOW()),
            ('7550e4e1-5e17-5052-aff3-541596f16aa4', 'Xero', 'https://api.ashbyhq.com/posting-api/job-board/xero', 'Scrapling', 'ashby', TRUE, 1440,
             NOW(), NOW(), NOW())
        ON CONFLICT (name) DO UPDATE SET
            base_url = EXCLUDED.base_url,
            method = EXCLUDED.method,
            parser = EXCLUDED.parser,
            enabled = EXCLUDED.enabled,
            crawl_frequency_minutes = EXCLUDED.crawl_frequency_minutes,
            terms_checked_at = COALESCE(job_sources.terms_checked_at, EXCLUDED.terms_checked_at),
            updated_at = NOW();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DELETE FROM job_sources WHERE name IN ('Absolute IT', 'Beyond Recruitment', 'Indeed NZ', 'NZ Government Jobs', 'Potentia', 'Recruit IT', 'SEEK NZ', 'Talent Army', 'Trade Me Jobs', 'Alpha Recruitment', 'Comspek International', 'Consult Recruitment', 'Digital Garage', 'Hays NZ', 'JobSeek', 'Madison Recruitment', 'PlaceMe IT', 'Randstad NZ', 'Robert Walters NZ', 'Salt NZ', 'TEKsystems NZ', 'Talent International NZ', 'Talent Vault', 'Tribe Recruitment', 'Younity', 'ZEIL', 'DOC Careers', 'GradConnection NZ', 'Inland Revenue Careers', 'MPI Careers', 'NZ Defence Careers', 'Prosple NZ', 'Student Job Search', 'ANZ NZ', 'ASB', 'Air New Zealand', 'BNZ', 'Datacom', 'EROAD', 'Fisher & Paykel', 'Fonterra', 'Gallagher', 'Halter', 'Jade Software', 'LIC', 'LinkedIn Jobs', 'MyJobSpace', 'NZ Police', 'One NZ', 'Orion Health', 'Pushpay', 'Rocket Lab', 'Ryman Healthcare', 'Serko', 'Spark NZ', 'Trimble', 'Westpac NZ', 'Xero')
          AND name NOT IN ('Salt', 'LinkedIn');
        """);
}
