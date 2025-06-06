﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web.Mvc;
using Npgsql;
using Student_Enrollment_System.Models;

namespace Student_Enrollment_System.Controllers
{
    public class StudentController : Controller
    {
        private readonly string _connectionString = ConfigurationManager.ConnectionStrings["DbConnection"].ConnectionString;
        
        
        public ActionResult Enrollment()
        {
            var sessionStudentId = Session["StudentId"];

            if (sessionStudentId == null)
            {
                return RedirectToAction("StudentLogIn", "Account");
            }
            
            if (!int.TryParse(sessionStudentId.ToString(), out int studentId))
            {
                return RedirectToAction("StudentLogIn", "Account");
            }
            
            try
            {
                // Load student data
                var student = GetStudentFromDatabase(studentId);
                if (student == null)
                {
                    ViewBag.ErrorMessage = "Student not found.";
                    return View("~/Views/Shared/Error.cshtml");
                }

                // Load programs for dropdown
                var programs = GetProgramsFromDatabase();
                ViewBag.Programs = programs;
                ViewBag.AcademicYears = GetAcademicYearsFromDatabase();
                ViewBag.YearLevels = GetYearLevelFromDatabase();
                ViewBag.Semesters = GetSemesterFromDatabase();
                ViewBag.Prerequisites = GetPrerequisitesFromDatabase();
                ViewBag.Courses = GetCoursesFromDatabase();
                return View("~/Views/Student/StudentEnroll.cshtml", student);
            }
            catch (Exception ex)
            {
                ViewBag.ErrorMessage = $"Error loading student data: {ex.Message}";
                return View("~/Views/Shared/Error.cshtml");
            }
        }
        
        [HttpPost]
        [Route("/Student/Enrollment/SubmitForm")]
        public ActionResult Enroll(EnrollmentViewModel model)
        {
            try
            {
                // Validate student data
                if (model?.Student == null)
                {
                    return Json(new { success = false, error = "Student information is missing." });
                }

                // Validate at least one course is selected
                if (!model.EnrollingCourses.Any())
                {
                    return Json(new { success = false, error = "Please select at least one course." });
                }

                using (var db = new NpgsqlConnection(_connectionString))
                {
                    db.Open();
                    using (var transaction = db.BeginTransaction())
                    {
                        try
                        {
                            var student = model.Student;
                            var currentAcademicYear = model.Enrollment.AcademicYear;
                            var currentSemester = model.Enrollment.Semester;

                            // 1. Check for existing pending or active enrollment
                            var enrollmentCheckCmd = new NpgsqlCommand(
                                @"SELECT COUNT(*) FROM ENROLLMENT 
                                  WHERE STUD_ID = @StudentId 
                                  AND AY_CODE = @AcademicYear
                                  AND ENROL_SEM = @Semester
                                  AND ENROL_STATUS IN ('Pending', 'Accepted')",
                                db, transaction);
                            
                            enrollmentCheckCmd.Parameters.AddWithValue("@StudentId", student.Id);
                            enrollmentCheckCmd.Parameters.AddWithValue("@AcademicYear", currentAcademicYear);
                            enrollmentCheckCmd.Parameters.AddWithValue("@Semester", currentSemester);
                            
                            var existingEnrollments = Convert.ToInt32(enrollmentCheckCmd.ExecuteScalar());
                            if (existingEnrollments > 0)
                            {
                                return Json(new { 
                                    success = false, 
                                    error = "Student already has an active/pending enrollment for this semester and academic year." 
                                });
                            }

                            // 2. Insert or Update Student
                            var studentExistsCmd = new NpgsqlCommand(
                                "SELECT COUNT(*) FROM STUDENT WHERE STUD_ID = @Id", db, transaction);
                            studentExistsCmd.Parameters.AddWithValue("@Id", student.Id);
                            var studentExists = Convert.ToInt32(studentExistsCmd.ExecuteScalar()) > 0;

                            if (!studentExists)
                            {
                                var insertStudentCmd = new NpgsqlCommand(@"
                                    INSERT INTO STUDENT 
                                    (STUD_ID, STUD_FNAME, STUD_LNAME, STUD_MNAME, STUD_HOME_ADDRESS, 
                                     STUD_CONTACT, STUD_EMAIL, STUD_YR_LEVEL, STUD_SEM, 
                                     PROG_ID, BSEC_CODE, STUD_STATUS)
                                    VALUES 
                                    (@Id, @FirstName, @LastName, @MiddleName, @HomeAddress, 
                                     @Contact, @Email, @YearLevel, @Semester, 
                                     @Program, @BlockSection, @Status)",
                                    db, transaction);

                                AddStudentParameters(insertStudentCmd, student);
                                insertStudentCmd.ExecuteNonQuery();
                            }
                            else
                            {
                                var updateStudentCmd = new NpgsqlCommand(@"
                                    UPDATE STUDENT SET
                                        STUD_FNAME = @FirstName,
                                        STUD_LNAME = @LastName,
                                        STUD_MNAME = @MiddleName,
                                        STUD_HOME_ADDRESS = @HomeAddress,
                                        STUD_CONTACT = @Contact,
                                        STUD_EMAIL = @Email,
                                        STUD_YR_LEVEL = @YearLevel,
                                        STUD_SEM = @Semester,
                                        PROG_ID = @Program,
                                        BSEC_CODE = @BlockSection,
                                        STUD_STATUS = @Status
                                    WHERE STUD_ID = @Id",
                                    db, transaction);

                                AddStudentParameters(updateStudentCmd, student);
                                updateStudentCmd.ExecuteNonQuery();
                            }

                            var enrollmentId = Guid.NewGuid().ToString();
                            var insertEnrollmentCmd = new NpgsqlCommand(@"
                                INSERT INTO ENROLLMENT 
                                (ENROL_ID, ENROL_STATUS, ENROL_DATE, ENROL_YR_LEVEL, ENROL_SEM, 
                                 STUD_ID, AY_CODE)
                                VALUES 
                                (@EnrollmentId, @Status, @EnrollmentDate, @YearLevel, @Semester,
                                 @StudentId, @AcademicYear)
                                RETURNING ENROL_ID",
                                db, transaction);

                            insertEnrollmentCmd.Parameters.AddWithValue("@EnrollmentId", enrollmentId);
                            insertEnrollmentCmd.Parameters.AddWithValue("@Status", "Pending");
                            insertEnrollmentCmd.Parameters.AddWithValue("@EnrollmentDate", DateTime.Now);
                            insertEnrollmentCmd.Parameters.AddWithValue("@YearLevel", model.Enrollment.YearLevel);
                            insertEnrollmentCmd.Parameters.AddWithValue("@Semester", model.Enrollment.Semester);
                            insertEnrollmentCmd.Parameters.AddWithValue("@StudentId", student.Id);
                            insertEnrollmentCmd.Parameters.AddWithValue("@AcademicYear", currentAcademicYear);

                            enrollmentId = (string)insertEnrollmentCmd.ExecuteScalar();

                            // 4. Insert selected courses into ENROLLING_COURSE table
                            foreach (var course in model.EnrollingCourses)
                            {
                                // First validate the course exists
                                var courseCheckCmd = new NpgsqlCommand(
                                    "SELECT COUNT(*) FROM COURSE WHERE CRS_CODE = @CourseCode", 
                                    db, transaction);
                                courseCheckCmd.Parameters.AddWithValue("@CourseCode", course.CourseCode);
                                var courseExists = Convert.ToInt32(courseCheckCmd.ExecuteScalar()) > 0;

                                if (!courseExists)
                                {
                                    transaction.Rollback();
                                    return Json(new { 
                                        success = false, 
                                        error = $"Course {course.CourseCode} does not exist." 
                                    });
                                }

                                var insertEnrollingCourseCmd = new NpgsqlCommand(@"
                                    INSERT INTO ENROLLING_COURSE 
                                    (CRS_CODE, ENROL_ID, SCHD_ID)
                                    VALUES 
                                    (@CourseCode, @EnrollmentId, @ScheduleId)",
                                    db, transaction);

                                insertEnrollingCourseCmd.Parameters.AddWithValue("@CourseCode", course.CourseCode);
                                insertEnrollingCourseCmd.Parameters.AddWithValue("@EnrollmentId", enrollmentId);
                                insertEnrollingCourseCmd.Parameters.AddWithValue("@ScheduleId", 
                                    course.ScheduleId > 0 ? (object)course.ScheduleId : DBNull.Value);

                                insertEnrollingCourseCmd.ExecuteNonQuery();
                            }

                            transaction.Commit();

                            return Json(new
                            {
                                success = true,
                                message = "Enrollment completed successfully.",
                                redirectUrl = Url.Action("Dashboard", "Student")
                            });
                        }
                        catch (Exception ex)
                        {
                            transaction.Rollback();
                            return Json(new
                            {
                                success = false,
                                error = "An error occurred during enrollment: " + ex.Message
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    error = "Server error: " + ex.Message
                });
            }
        }

        private void AddStudentParameters(NpgsqlCommand cmd, Student student)
        {
            cmd.Parameters.AddWithValue("@Id", student.Id);
            cmd.Parameters.AddWithValue("@FirstName", student.FirstName ?? "");
            cmd.Parameters.AddWithValue("@LastName", student.LastName ?? "");
            cmd.Parameters.AddWithValue("@MiddleName", student.MiddleName ?? "");
            cmd.Parameters.AddWithValue("@HomeAddress", student.HomeAddress ?? "");
            cmd.Parameters.AddWithValue("@Contact", student.Contact ?? "");
            cmd.Parameters.AddWithValue("@Email", student.Email ?? "");
            cmd.Parameters.AddWithValue("@YearLevel", student.YearLevel);
            cmd.Parameters.AddWithValue("@Semester", student.Semester);
            cmd.Parameters.AddWithValue("@Program", student.Program ?? "");
            cmd.Parameters.AddWithValue("@BlockSection", student.BlockSection ?? "");
            cmd.Parameters.AddWithValue("@Status", student.Status ?? "");
        }

            
        [HttpGet]
        [Route("/Student/Enrollment/GetCurriculum")]
        public ActionResult GetCurriculum(string progCode, string ayCode)
        {
            try
            {
                using (var db = new NpgsqlConnection(_connectionString))
                {
                    db.Open();
                    var cmd = new NpgsqlCommand(
                        "SELECT CUR_CODE FROM CURRICULUM WHERE PROG_CODE = @ProgCode AND AY_CODE = @AyCode", 
                        db);
            
                    cmd.Parameters.AddWithValue("@ProgCode", progCode);
                    cmd.Parameters.AddWithValue("@AyCode", ayCode);
            
                    var result = cmd.ExecuteScalar();
                    if (result == null)
                    {
                        return Json(new { success = false, message = "Curriculum not found" }, JsonRequestBehavior.AllowGet);
                    }
            
                    return Json(new { 
                        success = true, 
                        data = new { curCode = result.ToString() } 
                    }, JsonRequestBehavior.AllowGet);
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }
        
        [HttpGet]
        [Route("/Student/Enrollment/GetAvailableCourse")]
        public ActionResult GetAvailableCourse(int yearLevel, int semesterId, string progCode, string ayCode, string curCode)
        {
            try
            {
                // Validate inputs (same as before)
                if (yearLevel <= 0 || semesterId <= 0 || 
                    string.IsNullOrEmpty(progCode) || 
                    string.IsNullOrEmpty(ayCode) ||
                    string.IsNullOrEmpty(curCode))
                {
                    return Json(new { 
                        success = false,
                        message = "Invalid parameters"
                    }, JsonRequestBehavior.AllowGet);
                }

                using (var db = new NpgsqlConnection(_connectionString))
                {
                    db.Open();
                    
                    // Same SQL query as before
                    var cmd = new NpgsqlCommand(@"
                        SELECT 
                            c.CRS_CODE AS Code,
                            c.CRS_TITLE AS Title,
                            c.CRS_UNITS AS Units,
                            c.CRS_LEC AS LecHours,
                            c.CRS_LAB AS LabHours,
                            ct.CTG_NAME AS CategoryName,
                            ct.CTG_CODE AS CategoryCode,
                            s.SCHD_ID AS ScheduleId,
                            (
                                SELECT ARRAY_AGG(p.PREQ_CRS_CODE)
                                FROM PREREQUISITE p
                                WHERE p.CRS_CODE = c.CRS_CODE
                            ) AS Prerequisites,
                            ts.TSL_DAY AS Day,
                            ts.TSL_START_TIME AS StartTime,
                            ts.TSL_END_TIME AS EndTime,
                            r.ROOM_CODE AS RoomNumber,
                            p.PROF_NAME AS InstructorName,
                            bs.BSEC_NAME AS SectionName
                        FROM COURSE c
                        INNER JOIN CURRICULUM_COURSE cc ON c.CRS_CODE = cc.CRS_CODE
                        INNER JOIN CURRICULUM cur ON cc.CUR_CODE = cur.CUR_CODE
                        LEFT JOIN COURSE_CATEGORY ct ON c.CTG_CODE = ct.CTG_CODE
                        LEFT JOIN SCHEDULE s ON c.CRS_CODE = s.CRS_CODE
                        LEFT JOIN TIME_SLOT ts ON s.SCHD_ID = ts.SCHD_ID
                        LEFT JOIN ROOM r ON s.ROOM_ID = r.ROOM_ID
                        LEFT JOIN PROFESSOR p ON s.PROF_ID = p.PROF_ID
                        LEFT JOIN BLOCK_SECTION bs ON s.BSEC_CODE = bs.BSEC_CODE
                        WHERE cc.CUR_YEAR_LEVEL = @YearLevel
                        AND cc.CUR_SEMESTER = @SemesterId
                        AND cur.PROG_CODE = @ProgCode
                        AND cur.CUR_CODE = @CurCode
                        AND cur.AY_CODE = @AyCode
                        AND bs.AY_CODE = @AyCode
                        ORDER BY c.CRS_CODE, ts.TSL_DAY, ts.TSL_START_TIME", db);

                    // Same parameters as before
                    cmd.Parameters.AddWithValue("@YearLevel", yearLevel);
                    cmd.Parameters.AddWithValue("@SemesterId", semesterId);
                    cmd.Parameters.AddWithValue("@ProgCode", progCode);
                    cmd.Parameters.AddWithValue("@AyCode", ayCode);
                    cmd.Parameters.AddWithValue("@CurCode", curCode);

                    var courses = new List<dynamic>();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            courses.Add(new
                            {
                                Code = reader["Code"].ToString(),
                                Title = reader["Title"].ToString(),
                                Units = Convert.ToInt32(reader["Units"]),
                                LecHours = Convert.ToInt32(reader["LecHours"]),
                                LabHours = Convert.ToInt32(reader["LabHours"]),
                                CategoryName = reader["CategoryName"].ToString(),
                                CategoryCode = reader["CategoryCode"].ToString(),
                                Prerequisites = reader["Prerequisites"] as string[] ?? Array.Empty<string>(),
                                ScheduleId = reader.IsDBNull(reader.GetOrdinal("ScheduleId")) ? 0 : Convert.ToInt32(reader["ScheduleId"]),
                                Day = reader.IsDBNull(reader.GetOrdinal("Day")) ? null : reader["Day"].ToString(),
                                StartTime = reader.IsDBNull(reader.GetOrdinal("StartTime")) ? TimeSpan.Zero : (TimeSpan)reader["StartTime"],
                                EndTime = reader.IsDBNull(reader.GetOrdinal("EndTime")) ? TimeSpan.Zero : (TimeSpan)reader["EndTime"],
                                RoomNumber = reader.IsDBNull(reader.GetOrdinal("RoomNumber")) ? null : reader["RoomNumber"].ToString(),
                                InstructorName = reader.IsDBNull(reader.GetOrdinal("InstructorName")) ? null : reader["InstructorName"].ToString(),
                                SectionName = reader.IsDBNull(reader.GetOrdinal("SectionName")) ? null : reader["SectionName"].ToString()
                            });
                        }
                    }

                    // Group by course and format times
                    var groupedCourses = courses
                        .GroupBy(c => c.Code)
                        .Select(g => new
                        {
                            Category = g.First().CategoryName,
                            Code = g.Key,
                            LabHours = g.First().LabHours,
                            LecHours = g.First().LecHours,
                            Prerequisites = g.First().Prerequisites,
                            Title = g.First().Title,
                            Units = g.First().Units,
                            ScheduleDetails = g.Where(x => x.ScheduleId != 0)
                                            .GroupBy(s => s.ScheduleId)
                                            .Select(sg => new
                                            {
                                                InstructorName = sg.First().InstructorName ?? "TBA",
                                                RoomNumber = sg.First().RoomNumber ?? "TBA",
                                                ScheduleId = sg.Key,
                                                SectionName = sg.First().SectionName ?? "TBA",
                                                TimeSlots = sg.Select(s => new
                                                {
                                                    Day = s.Day,
                                                    EndTime = new { 
                                                        Hours = s.EndTime.Hours,
                                                        Minutes = s.EndTime.Minutes
                                                    },
                                                    ScheduleId = s.ScheduleId,
                                                    StartTime = new {
                                                        Hours = s.StartTime.Hours,
                                                        Minutes = s.StartTime.Minutes
                                                    }
                                                })
                                                .OrderBy(s => s.Day)
                                                .ThenBy(s => s.StartTime.Hours)
                                                .ThenBy(s => s.StartTime.Minutes)
                                                .ToList()
                                            })
                                            .ToList()
                        })
                        .OrderBy(c => c.Code)
                        .ToList();

                    return Json(new
                    {
                        success = true,
                        data = groupedCourses
                    }, JsonRequestBehavior.AllowGet);
                }
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "An error occurred while retrieving courses",
                    error = ex.Message
                }, JsonRequestBehavior.AllowGet);
            }
        }

        private Student GetStudentFromDatabase(int studentId)
        {
            using (var conn = new NpgsqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new NpgsqlCommand(
                    @"SELECT 
                        stud_id, stud_fname, stud_lname, stud_mname, 
                        stud_email, stud_dob, stud_contact, stud_city_address, 
                        stud_home_address, stud_district, stud_is_first_gen, 
                        stud_yr_level, stud_major, stud_status, stud_sem, 
                        bsec_code, prog_code
                      FROM student
                      WHERE stud_id = @StudentId", conn))
                {
                    cmd.Parameters.AddWithValue("@StudentId", studentId);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return new Student
                            {
                                Id = reader.GetInt32(0),
                                FirstName = reader.GetString(1),
                                LastName = reader.GetString(2),
                                MiddleName = reader.IsDBNull(3) ? null : reader.GetString(3),
                                Email = reader.GetString(4),
                                Birthdate = reader.GetDateTime(5),
                                Contact = reader.GetString(6),
                                CityAddress = reader.IsDBNull(7) ? null : reader.GetString(7),
                                HomeAddress = reader.IsDBNull(8) ? null : reader.GetString(8),
                                District = reader.IsDBNull(9) ? null : reader.GetString(9),
                                IsFirstGen = reader.GetBoolean(10),
                                YearLevel = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                                Major = reader.IsDBNull(12) ? null : reader.GetString(12),
                                Status = reader.IsDBNull(13) ? null : reader.GetString(13),
                                Semester = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                                BlockSection = reader.IsDBNull(15) ? null : reader.GetString(15),
                                ProgramCode = reader.IsDBNull(16) ? null : reader.GetString(16)
                            };
                        }
                    }
                }
            }
            return null; // Return null if no student found
        }
        
        private List<Course> GetCoursesFromDatabase()
        {
            var courses = new List<Course>();

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new NpgsqlCommand("SELECT \"crs_code\", \"crs_title\", \"crs_units\", \"crs_lec\", \"crs_lab\", \"ctg_code\" FROM \"course\"", conn))
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            courses.Add(new Course
                            {
                                Code = reader.GetString(0),
                                Title = reader.GetString(1),
                                Units = reader.GetInt32(2),
                                LecHours = reader.GetInt32(3),
                                LabHours = reader.GetInt32(4),
                                CategoryCode = reader.GetString(5)
                            });
                        }
                    }
                }
            }

            return courses;
        }
        
        private List<Prerequisite> GetPrerequisitesFromDatabase()
        {
            var prerequisites = new List<Prerequisite>();

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new NpgsqlCommand("SELECT \"crs_code\", \"preq_crs_code\" FROM \"prerequisite\"", conn))
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            prerequisites.Add(new Prerequisite
                            {
                                CourseCode = reader.GetString(0),
                                PrerequisiteCourseCode = reader.GetString(1)
                            });
                        }
                    }
                }
            }

            return prerequisites;
        }

        private List<Program> GetProgramsFromDatabase()
        {
            var programs = new List<Program>();

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new NpgsqlCommand("SELECT \"prog_code\", \"prog_title\" FROM \"program\"", conn))
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            programs.Add(new Program
                            {
                                Code = reader.GetString(0),
                                Title = reader.GetString(1)
                            });
                        }
                    }
                }
            }
            return programs;
        }
        private List<AcademicYear> GetAcademicYearsFromDatabase()
        {
            var academicYears = new List<AcademicYear>();

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new NpgsqlCommand("SELECT \"ay_code\", \"ay_start_year\", \"ay_end_year\" FROM \"academic_year\"", conn))
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            academicYears.Add(new AcademicYear
                            {
                                Code = reader.GetString(0),
                                StartYear = reader.GetInt16(1),
                                EndYear = reader.GetInt16(2),
                            });
                        }
                    }
                }
            }

            return academicYears;
        }
        private List<YearLevel> GetYearLevelFromDatabase()
        {
            var yearLevel = new List<YearLevel>();

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new NpgsqlCommand("SELECT \"yrl_id\", \"yrl_title\" FROM \"year_level\" ", conn))
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            yearLevel.Add(new YearLevel
                            {
                                Id = reader.GetInt16(0),
                                Title = reader.GetString(1),
                            });
                        }
                    }
                }
            }

            return yearLevel;
        }
        private List<Semester> GetSemesterFromDatabase()
        {
            var semesters = new List<Semester>();

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new NpgsqlCommand("SELECT \"sem_id\", \"sem_name\" FROM \"semester\"", conn))
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            semesters.Add(new Semester
                            {
                                Id = reader.GetInt16(0),
                                Name = reader.GetString(1)
                            });
                        }
                    }
                }
            }

            return semesters;
        }
        public ActionResult Dashboard()
        {
            return View("~/Views/Student/Dashboard.cshtml");
        }
        [HttpGet]
        public ActionResult ProfileView()
        {
            // View is located at Views/Account/LogIn.cshtml
            return View("~/Views/Student/StudentProfile.cshtml");
        }
        
        [HttpGet]
        public ActionResult Grade()
        {
            // View is located at Views/Account/LogIn.cshtml
            return View("~/Views/Student/ViewGrades.cshtml");
        }

        [HttpGet]
        public ActionResult Schedule()
        {
            // View is located at Views/Account/LogIn.cshtml
            return View("~/Views/Student/ClassSchedule.cshtml");
        }
        
    }
}