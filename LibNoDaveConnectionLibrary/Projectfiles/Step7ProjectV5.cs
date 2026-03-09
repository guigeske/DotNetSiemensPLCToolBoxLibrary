/*
 * Step7ProjectV5.cs - Parser for Siemens Step7 V5 PLC Projects
 *
 * This class provides comprehensive parsing of Step7 V5 project files (.s7p, .s7l),
 * including support for zipped project archives. It extracts:
 * - Hardware configurations (Stations, CPUs, Communication Processors)
 * - Network configurations (Ethernet, Profibus, MPI)
 * - Program structures (blocks, symbols, source files)
 * - Security settings (CPU passwords)
 *
 * The parser reads proprietary DBF database files and binary configuration files
 * within the Step7 project structure, building a complete object model of the
 * PLC system configuration.
 *
 * The parser uses lazy loading for performance - project structure is only
 * parsed when properties like CPUFolders or S7ProgrammFolders are first accessed.
 */

using DotNetSiemensPLCToolBoxLibrary.DataTypes;
using DotNetSiemensPLCToolBoxLibrary.DataTypes.Hardware.Step7V5;
using DotNetSiemensPLCToolBoxLibrary.DataTypes.Network;
using DotNetSiemensPLCToolBoxLibrary.DataTypes.Projectfolders.Step7V5;
using DotNetSiemensPLCToolBoxLibrary.General;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;

namespace DotNetSiemensPLCToolBoxLibrary.Projectfiles
{
    /// <summary>
    /// Represents a Siemens Step7 V5 project and provides access to its complete structure.
    /// Supports both file system and ZIP-based projects with lazy loading of project components.
    /// Implements IDisposable to properly clean up ZIP file handles.
    /// </summary>
    public class Step7ProjectV5 : Project, IDisposable
    {
        #region Object Type Constants
        // Object type identifiers used in Step7 V5 DBF databases (OBJTYP field in HOBJECT1.DBF)
        // to classify hardware components and network interfaces.        

        /// <summary>Siemens S7-300 PLC station type identifier</summary>
        private const int objectType_Simatic300 = 1314969;

        /// <summary>Siemens S7-400 PLC station type identifier</summary>
        private const int objectType_Simatic400 = 1314970;

        /// <summary>Siemens S7-400H redundant PLC station type identifier</summary>
        private const int objectType_Simatic400H = 1315650;

        /// <summary>Siemens RTX distributed I/O station type identifier</summary>
        private const int objectType_SimaticRTX = 1315651;

        /// <summary>Ethernet interface integrated in S7-300 series CPU</summary>
        private const int objectType_EternetInCPU3xx = 2364796;

        /// <summary>Ethernet interface integrated in S7-300 series CPU (alternative type, e.g., 6ES7 318-3EL00-0AB0 with single port)</summary>
        private const int objectType_EternetInCPU3xx_2 = 2364572;

        /// <summary>Ethernet interface integrated in S7-300F fail-safe CPU</summary>
        private const int objectType_EternetInCPU3xxF = 2364818;

        /// <summary>Ethernet interface integrated in S7-400 series CPU</summary>
        private const int objectType_EternetInCPU4xx = 2364763;

        /// <summary>Ethernet interface integrated in RTX CPU (e.g., 6ES7 611-4SB00-0YB7)</summary>
        private const int objectType_EternetInCPURTX = 2364315;

        private const int objectType_Eternet319= 2364813;

        private const int objectType_EternetPNIO = 2364589;

        /// <summary>MPI/DP (Multi Point Interface/Profibus) interface integrated in CPU</summary>
        private const int objectType_MpiDPinCPU = 1314972;

        /// <summary>MPI/DP interface module for S7-400 series</summary>
        private const int objectType_MpiDP400 = 1315038;

        /// <summary>MPI/DP interface module for S7-300 series</summary>
        private const int objectType_MpiDP300 = 1315016;

        #endregion

        #region Private Fields

        /// <summary>Path to the offline block database file (BSTCNTOF.DBF)</summary>
        private string _offlineblockdb;

        /// <summary>Flag to include deleted items (marked as deleted in DBF tables) in the project structure</summary>
        internal bool _showDeleted = false;

        /// <summary>
        /// ZIP file handler for compressed project archives.
        /// Abstracts access to both file system and ZIP-based projects.
        /// </summary>
        internal ZipHelper _ziphelper = new ZipHelper(null);

        /// <summary>Name of the .s7p or .s7l project file (or its path within a ZIP archive)</summary>
        internal string _projectfilename;

        /// <summary>
        /// Gets the appropriate directory separator based on whether the project is zipped.
        /// Returns '/' for ZIP files, platform-specific separator for file system.
        /// </summary>
        internal char _DirSeperator
        {
            get
            {
                if (!_ziphelper.IsZipped())
                    return Path.DirectorySeparatorChar;
                else
                    return '/';
            }
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new Step7 V5 project from a file path.
        /// </summary>
        /// <param name="projectfile">Path to .s7p, .s7l, or .zip file containing the project</param>
        /// <param name="showDeleted">If true, includes deleted items marked in the project database</param>
        public Step7ProjectV5(string projectfile, bool showDeleted)
            : this(projectfile, showDeleted, null)
        { }

        /// <summary>
        /// Initializes a new Step7 V5 project from a file path with custom text encoding.
        /// </summary>
        /// <param name="projectfile">Path to .s7p, .s7l, or .zip file containing the project</param>
        /// <param name="showDeleted">If true, includes deleted items marked in the project database</param>
        /// <param name="prEn">Text encoding for project strings. If null, detected from Global/Language file or defaults to ISO-8859-1</param>
        public Step7ProjectV5(string projectfile, bool showDeleted, Encoding prEn)
        {
            _projectfilename = projectfile;

            if (projectfile.ToLower().EndsWith("zip"))
            {
                this._ziphelper = new ZipHelper(projectfile);
                if (this._ziphelper.IsZipFile)
                {
                    _projectfilename = this._ziphelper.GetFirstZipEntryWithEnding(".s7p");
                    if (string.IsNullOrEmpty(_projectfilename))
                        _projectfilename = this._ziphelper.GetFirstZipEntryWithEnding(".s7l");
                }
                if (string.IsNullOrEmpty(_projectfilename))
                    throw new Exception("Zip-File contains no valid Step7 Project !");
            }

            ProjectFile = projectfile;
            ProjectFolder = _projectfilename.Substring(0, _projectfilename.LastIndexOf(_DirSeperator)) + _DirSeperator;

            ProjectEncoding = (prEn ?? Encoding.GetEncoding("ISO-8859-1"));
            var lngFile = _ziphelper.GetReadStream(ProjectFolder + "Global" + _DirSeperator + "Language");
            if (prEn == null && lngFile != null)
            {
                var rd = new StreamReader(lngFile);
                string line;
                while ((line = rd.ReadLine()) != null)
                {
                    if (line != "0")
                    {
                        int code;
                        if (int.TryParse(line, out code))
                        {
                            var enc = Encoding.GetEncodings().FirstOrDefault(x => x.CodePage == code);
                            if (enc != null)
                            {
                                ProjectEncoding = enc.GetEncoding();
                                break;
                            }
                        }
                    }
                }
            }

            LoadProjectHeader(showDeleted);
        }

        /// <summary>
        /// Initializes a new Step7 V5 project from a stream (typically a ZIP archive).
        /// </summary>
        /// <param name="projectfile">Stream containing a zipped Step7 project</param>
        /// <param name="showDeleted">If true, includes deleted items marked in the project database</param>
        /// <param name="prEn">Text encoding for project strings. If null, detected from Global/Language file or defaults to ISO-8859-1</param>
        public Step7ProjectV5(Stream projectfile, bool showDeleted, Encoding prEn)
        {
            this._ziphelper = ZipHelper.GetZipHelper(projectfile);
            _projectfilename = this._ziphelper.GetFirstZipEntryWithEnding(".s7p");
            if (string.IsNullOrEmpty(_projectfilename))
                _projectfilename = this._ziphelper.GetFirstZipEntryWithEnding(".s7l");
            if (string.IsNullOrEmpty(_projectfilename))
                throw new Exception("Zip-File contains no valid Step7 Project !");

            ProjectFile = _projectfilename;
            ProjectFolder = _projectfilename.Substring(0, _projectfilename.LastIndexOf(_DirSeperator)) + _DirSeperator;

            ProjectEncoding = (prEn ?? Encoding.GetEncoding("ISO-8859-1"));
            var lngFile = _ziphelper.GetReadStream(ProjectFolder + "Global" + _DirSeperator + "Language");
            if (prEn == null && lngFile != null)
            {
                var rd = new StreamReader(lngFile);
                string line;
                while ((line = rd.ReadLine()) != null)
                {
                    if (line != "0")
                    {
                        int code;
                        if (int.TryParse(line, out code))
                        {
                            var enc = Encoding.GetEncodings().FirstOrDefault(x => x.CodePage == code);
                            if (enc != null)
                            {
                                ProjectEncoding = enc.GetEncoding();
                                break;
                            }
                        }
                    }
                }
            }

            LoadProjectHeader(showDeleted);
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Loads basic project metadata (name, description) from the .s7p/.s7l header.
        /// Also determines the offline block database path.
        /// </summary>
        /// <param name="showDeleted">If true, includes deleted items in subsequent parsing</param>
        private void LoadProjectHeader(bool showDeleted)
        {
            _showDeleted = showDeleted;

            // Read project information from the .s7p/.s7l header file
            Stream fsProject = _ziphelper.GetReadStream(_projectfilename);

            // Read number of bytes from the project file
            byte[] projectFile = new byte[_ziphelper.GetStreamLength(_projectfilename, fsProject)];
            fsProject.Read(projectFile, 0, projectFile.Length);
            fsProject.Close();

            ProjectName = Encoding.UTF7.GetString(projectFile, 5, projectFile[4]);

            int descStart = 5 + projectFile[4] + 2;
            int descCount = Math.Min(projectFile[projectFile[4] + 6], projectFile.Length - 1 - descStart);
            ProjectDescription = Encoding.UTF7.GetString(projectFile, descStart, descCount);
            // Finished reading project header

            _offlineblockdb = ProjectFolder + "ombstx" + _DirSeperator + "offline" + _DirSeperator + "BSTCNTOF.DBF";
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Returns a string representation of the project including ZIP and deleted item flags.
        /// </summary>
        /// <returns>Project name with optional "(zipped)" and "(show deleted)" indicators</returns>
        public override string ToString()
        {
            string retVal = base.ToString();
            if (_ziphelper.IsZipped())
                retVal += "(zipped)";
            if (_showDeleted == true)
                retVal += " (show deleted)";
            return retVal;
        }

        internal bool hasChanges;

        /// <summary>
        /// Finalizer to ensure ZIP file handles are released if Dispose is not called.
        /// </summary>
        ~Step7ProjectV5()
        {
            Dispose();
        }

        /// <summary>
        /// Releases ZIP file resources. Should be called when finished with the project.
        /// </summary>
        public void Dispose()
        {
            if (hasChanges)
            {
                hasChanges = false;
                //ZipHelper.SaveZip(_zipfile);
            }
            if (_ziphelper != null)
                _ziphelper.Close();
        }

        #endregion

        #region Public Properties

        /*
        private Step7ProjectFolder _step7ProjectStructure;

        public Step7ProjectFolder Step7ProjectStructure
        {
            get
            {
                if (!_projectLoaded)
                    LoadProject();
                return _step7ProjectStructure;
            }
            set { _step7ProjectStructure = value; }
        }
        */

        private List<CPUFolder> _cpuFolders;

        /// <summary>
        /// Gets the list of CPU folders found in the project (S7-300, S7-400, ET200S).
        /// Triggers lazy loading of the complete project structure on first access.
        /// </summary>
        public List<CPUFolder> CPUFolders
        {
            get
            {
                if (!_projectLoaded)
                    LoadProject();
                return _cpuFolders;
            }
            set { _cpuFolders = value; }
        }

        private List<CPFolder> _cpFolders;

        /// <summary>
        /// Gets the list of Communication Processor (CP) folders found in the project.
        /// CPs handle network communication (Ethernet, Profibus, etc.).
        /// Triggers lazy loading of the complete project structure on first access.
        /// </summary>
        public List<CPFolder> CPFolders
        {
            get
            {
                if (!_projectLoaded)
                    LoadProject();
                return _cpFolders;
            }
            set { _cpFolders = value; }
        }

        private List<S7ProgrammFolder> _s7ProgrammFolders;

        /// <summary>
        /// Gets the list of S7 program folders containing PLC programs and blocks.
        /// Triggers lazy loading of the complete project structure on first access.
        /// </summary>
        public List<S7ProgrammFolder> S7ProgrammFolders
        {
            get
            {
                if (!_projectLoaded)
                    LoadProject();
                return _s7ProgrammFolders;
            }
            set { _s7ProgrammFolders = value; }
        }

        private List<BlocksOfflineFolder> _blocksOfflineFolders;

        /// <summary>
        /// Gets the list of offline block folders containing compiled PLC blocks (FC, FB, DB, etc.).
        /// Triggers lazy loading of the complete project structure on first access.
        /// </summary>
        public List<BlocksOfflineFolder> BlocksOfflineFolders
        {
            get
            {
                if (!_projectLoaded)
                    LoadProject();
                return _blocksOfflineFolders;
            }
            set { _blocksOfflineFolders = value; }
        }

        /// <summary>
        /// Gets the project type identifier.
        /// </summary>
        /// <returns>Always returns ProjectType.Step7 for Step7 V5 projects</returns>
        public override ProjectType ProjectType
        {
            get { return ProjectType.Step7; }
        }

        #endregion

        #region Project Loading

        /// <summary>
        /// Performs the actual project parsing by reading DBF databases and binary files.
        /// Called automatically on first property access due to lazy loading pattern.
        /// Parses hardware structure, network configurations, program blocks, and symbol tables.
        /// </summary>
        protected override void LoadProject()
        {
            _projectLoaded = true;

            // ===== SECTION 1: Initialize Collections =====
            // Initialize collections for parsed project components
            // Collections will be populated as we traverse the DBF database relationships
            ProjectStructure = new Step7ProjectFolder() { Project = this };
            CPUFolders = new List<CPUFolder>();
            CPFolders = new List<CPFolder>();
            S7ProgrammFolders = new List<S7ProgrammFolder>();
            BlocksOfflineFolders = new List<BlocksOfflineFolder>();

            ProjectStructure.Name = this.ToString();

            var stations = new List<StationConfigurationFolder>();

            List<CPFolder> DPFolders = new List<CPFolder>();

            // ===== SECTION 2: Parse Stations =====
            // Parse station configurations (S7-300, S7-400, S7-400H, RTX) from hOmSave7/s7hstatx/HOBJECT1.DBF
            // Stations are the top-level hardware containers that hold CPUs and other modules
            // Also collect MPI/DP interface objects for later linking to CPUs
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hstatx" + _DirSeperator + "HOBJECT1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hstatx" + _DirSeperator + "HOBJECT1.DBF", _ziphelper, _DirSeperator);
                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        if ((int)row["OBJTYP"] == objectType_Simatic300 ||
                            (int)row["OBJTYP"] == objectType_Simatic400 ||
                            (int)row["OBJTYP"] == objectType_Simatic400H ||
                            (int)row["OBJTYP"] == objectType_SimaticRTX)
                        {
                            var x = new StationConfigurationFolder() { Project = this, Parent = ProjectStructure };
                            x.Name = ((string)row["Name"]).Replace("\0", "");
                            if ((bool)row["DELETED_FLAG"]) x.Name = "$$_" + x.Name;
                            x.ID = (int)row["ID"];
                            x.UnitID = (int)row["UNITID"];
                            x.ObjTyp = (int)row["OBJTYP"];
                            switch ((int)row["OBJTYP"])
                            {
                                case objectType_Simatic300:
                                    x.StationType = PLCType.Simatic300;
                                    break;

                                case objectType_Simatic400:
                                    x.StationType = PLCType.Simatic400;
                                    break;

                                case objectType_Simatic400H:
                                    x.StationType = PLCType.Simatic400H;
                                    break;

                                case objectType_SimaticRTX:
                                    x.StationType = PLCType.SimaticRTX;
                                    break;
                            }
                            x.Parent = ProjectStructure;
                            ProjectStructure.SubItems.Add(x);
                            stations.Add(x);
                            _allFolders.Add(x);
                        }
                        else if (Convert.ToInt32(row["OBJTYP"]) == objectType_MpiDPinCPU)
                        {
                            var dp = new CPFolder();
                            dp.UnitID = Convert.ToInt32(row["UNITID"]);//is UNITID in CPUFolder
                            dp.ID = Convert.ToInt32(row["ID"]);
                            DPFolders.Add(dp);
                            _allFolders.Add(dp);
                        }
                    }
                }
            }

            // ===== SECTION 3: Link Hardware to Stations =====
            // Establish relationships between MPI/DP interfaces and stations using HRELATI1.DBF
            // Relationship ID 1315820 indicates MPI/DP interface ownership
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hstatx" + _DirSeperator + "HRELATI1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hstatx" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);
                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        if (Convert.ToInt32(row["RELID"]) == 1315820)
                        {
                            int TobjType = Convert.ToInt32(row["TOBJTYP"]);

                            if (TobjType == objectType_MpiDP400 || TobjType == objectType_MpiDP300)
                            {
                                var dp = DPFolders.FirstOrDefault(x => x.ID == Convert.ToInt32(row["SOBJID"]));
                                if (dp != null)
                                {
                                    if (dp.IdTobjId == null) dp.IdTobjId = new List<int>();
                                    dp.IdTobjId.Add(Convert.ToInt32(row["TOBJID"]));
                                }
                            }
                        }
                    }
                }
            }

            /*
            //Get The HW Folder for the Station...
            if (ZipHelper.FileExists(_zipfile,ProjectFolder + "hOmSave7" + _DirSeperator + "s7hstatx" + _DirSeperator + "HRELATI1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hstatx" + _DirSeperator + "HRELATI1.DBF", _zipfile, _DirSeperator);
                foreach (var y in Step7ProjectStructure.SubItems)
                {
                    if (y.GetType() == typeof (StationConfigurationFolder))
                    {
                        var z = (StationConfigurationFolder) y;
                        foreach (DataRow row in dbfTbl.Rows)
                        {
                            if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                            {
                                if ((int)row["SOBJID"] == z.ID && (int)row["RELID"] == 1315838)
                                {
                                    var x = new CPUFolder() {Project = this};
                                    x.UnitID = Convert.ToInt32(row["TUNITID"]);
                                    x.TobjTyp = Convert.ToInt32(row["TOBJTYP"]);
                                    x.CpuType = z.StationType;
                                    bool add = true;
                                    foreach (Step7ProjectFolder tmp in z.SubItems)
                                    {
                                        if (tmp.GetType() == typeof (CPUFolder) && ((CPUFolder) tmp).UnitID == x.UnitID)
                                            add = false;
                                    }
                                    if (add)
                                    {
                                        x.Parent = z;
                                        z.SubItems.Add(x);
                                        CPUFolders.Add(x);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            */

            // ===== SECTION 4: Parse Communication Processors =====
            // Parse CP (Communication Processor) modules from s7wb53ax and s7w1105x databases
            // CPs handle network communication (Ethernet, Profibus, etc.)
            // Build parent-child relationships for sub-modules using SubModulNumber
            string cp300Base = ProjectFolder + "hOmSave7" + _DirSeperator + "s7wb53ax" + _DirSeperator;
            string cp400Base = ProjectFolder + "hOmSave7" + _DirSeperator + "s7w1105x" + _DirSeperator;

            List<string> cpPaths = new List<string>();
            string cp300 = cp300Base + "HOBJECT1.DBF";
            if (_ziphelper.FileExists(cp300))
            {
                cpPaths.Add(cp300);
            }
            string cp400 = cp400Base + "HOBJECT1.DBF";
            if (_ziphelper.FileExists(cp400))
            {
                cpPaths.Add(cp400);
            }
            foreach (string cpPath in cpPaths)
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(cpPath, _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        var cp = new CPFolder() { Project = this };
                        cp.ID = Convert.ToInt32(row["ID"]);
                        cp.UnitID = Convert.ToInt32(row["UNITID"]);
                        cp.TobjTyp = Convert.ToInt32(row["OBJTYP"]);
                        cp.Name = ((string)row["Name"]).Replace("\0", "");
                        if (Convert.ToBoolean(row["DELETED_FLAG"])) cp.Name = "$$_" + cp.Name;
                        cp.Rack = Convert.ToInt32(row["SUBSTATN"]);
                        cp.Slot = Convert.ToInt32(row["MODULN"]);
                        cp.SubModulNumber = Convert.ToInt32(row["SUBMODN"]);
                        CPFolders.Add(cp);
                        _allFolders.Add(cp);
                    }
                }
            }
            //add subitem to parent
            foreach (var cp in CPFolders.Where(x => x.SubModulNumber > 0))
            {
                var parent = CPFolders.FirstOrDefault(x => x.ID == cp.UnitID);
                if (parent != null) parent.SubModul = cp;
            }

            // ===== SECTION 5: Link CPs to Stations =====
            // Link parsed CPs to their parent stations using HRELATI1.DBF relationships
            // RELID 1315827: CP belongs to station
            // RELID 64: CP network interface references
            List<string> cpFolderPaths = new List<string>();
            string cp300Folder = cp300Base + "HRELATI1.DBF";
            if (_ziphelper.FileExists(cp300Folder))
            {
                cpFolderPaths.Add(cp300Folder);
            }
            string cp400Folder = cp400Base + "HRELATI1.DBF";
            if (_ziphelper.FileExists(cp400Folder))
            {
                cpFolderPaths.Add(cp400Folder);
            }
            foreach (string cpFolderPath in cpFolderPaths)
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(cpFolderPath, _ziphelper, _DirSeperator);
                foreach(DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        int relID = Convert.ToInt32(row["RELID"]);
                        if (relID == 1315827)
                        {
                            var cpu = (StationConfigurationFolder)ProjectStructure.SubItems.FirstOrDefault(x => x.ID == Convert.ToInt32(row["TUNITID"]));
                            var cp = CPFolders.FirstOrDefault(x => x.ID == Convert.ToInt32(row["SOBJID"]));
                            if (cpu != null && cp != null) cpu.SubItems.Add(cp);
                        }
                        else if (relID == 64)
                        {
                            var cp = CPFolders.FirstOrDefault(x => x.ID == Convert.ToInt32(row["SOBJID"]));
                            if (cp != null)
                            {
                                if (cp.TobjId == null) cp.TobjId = new List<int>();
                                cp.TobjId.Add(Convert.ToInt32(row["TOBJID"]));
                            }
                        }
                    }
                }
            }

            // ===== SECTION 6: Parse CPU Folders =====
            // Parse CPU folders from multiple hardware databases (s7hk31ax, s7hkcomx, s7hk41ax)
            // Each CPU type (S7-300, ET200S, S7-400) is stored in a separate database folder
            // Special handling for S7-400H redundant systems (backup CPU object type 1315656)

            // Get The CPU 300 Folders
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk31ax" + _DirSeperator + "HRELATI1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk31ax" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);
                foreach (var y in ProjectStructure.SubItems)
                {
                    if (y.GetType() == typeof(StationConfigurationFolder))
                    {
                        var z = (StationConfigurationFolder)y;
                        foreach (DataRow row in dbfTbl.Rows)
                        {
                            if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                            {
                                if ((int)row["TUNITID"] == z.ID && (int)row["TOBJTYP"] == 1314972)
                                //((int)row["TUNITTYP"] == 1314969 || (int)row["TUNITTYP"] == 1314969 || (int)row["TUNITTYP"] == 1314969))
                                {
                                    var x = new CPUFolder() { Project = this };
                                    x.UnitID = Convert.ToInt32(row["TUNITID"]);
                                    x.TobjTyp = Convert.ToInt32(row["TOBJTYP"]);
                                    x.CpuType = z.StationType;
                                    x.ID = Convert.ToInt32(row["SOBJID"]);
                                    x.Parent = z;
                                    z.SubItems.Add(x);
                                    CPUFolders.Add(x);
                                    _allFolders.Add(x);
                                }
                            }
                        }
                    }
                }
            }

            //Get The CPU 300 ET200s Folders
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkcomx" + _DirSeperator + "HRELATI1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkcomx" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);
                foreach (var y in ProjectStructure.SubItems)
                {
                    if (y.GetType() == typeof(StationConfigurationFolder))
                    {
                        var z = (StationConfigurationFolder)y;
                        foreach (DataRow row in dbfTbl.Rows)
                        {
                            if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                            {
                                if ((int)row["TUNITID"] == z.ID && (int)row["TOBJTYP"] == 1314972)
                                //((int)row["TUNITTYP"] == 1314969 || (int)row["TUNITTYP"] == 1314969 || (int)row["TUNITTYP"] == 1314969))
                                {
                                    var x = new CPUFolder() { Project = this };
                                    x.UnitID = Convert.ToInt32(row["TUNITID"]);
                                    x.TobjTyp = Convert.ToInt32(row["TOBJTYP"]);
                                    x.CpuType = z.StationType;
                                    x.ID = Convert.ToInt32(row["SOBJID"]);
                                    x.CpuType = PLCType.SimaticET200;
                                    x.Parent = z;
                                    z.SubItems.Add(x);
                                    CPUFolders.Add(x);
                                    _allFolders.Add(x);
                                }
                            }
                        }
                    }
                }
            }
            //Get The CPU 400 Folders
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk41ax" + _DirSeperator + "HRELATI1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk41ax" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);

                foreach (var y in ProjectStructure.SubItems)
                {
                    if (y.GetType() == typeof(StationConfigurationFolder))
                    {
                        var z = (StationConfigurationFolder)y;
                        foreach (DataRow row in dbfTbl.Rows)
                        {
                            if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                            {
                                if ((int)row["TUNITID"] == z.ID && 
                                    ((int)row["TOBJTYP"] == 1314972 || (int)row["TOBJTYP"] == 1315656 /* BackupCPU bei H Sys */) &&
                                    (CPUFolders.FirstOrDefault(folder => folder.ID == (int)row["SOBJID"] && folder.CpuType == z.StationType) == null)) /* skip over duplicate CPU folders */
                                //((int)row["TUNITTYP"] == 1314969 || (int)row["TUNITTYP"] == 1314969 || (int)row["TUNITTYP"] == 1314969))
                                {
                                    var x = new CPUFolder() { Project = this };
                                    x.UnitID = Convert.ToInt32(row["TUNITID"]);
                                    x.TobjTyp = Convert.ToInt32(row["TOBJTYP"]);
                                    x.CpuType = z.StationType;
                                    x.ID = Convert.ToInt32(row["SOBJID"]);
                                    x.Parent = z;
                                    z.SubItems.Add(x);
                                    CPUFolders.Add(x);
                                    _allFolders.Add(x);
                                }
                            }
                        }
                    }
                }
            }

            // ===== SECTION 7: Extract CPU Hardware Information (MLFB) =====
            // Extract CPU order numbers (MLFB) from binary .s7h files
            // Uses pattern matching to find CPU module information in hardware configuration
            // The MLFB (ordering number) identifies the exact CPU model (e.g., 6ES7 318-3EL00-0AB0)
            foreach (var y in CPUFolders)
            {
                try
                {
                    string filepath = ProjectFolder + "hOmSave7" + _DirSeperator +
                        "s7hstatx" + _DirSeperator + y.UnitID.ToString("x") + ".s7h";
                    if (!_ziphelper.FileExists(filepath))
                        continue; //In some projects the s7h files does not exist, but is created after a HWconf recompile (v5.6 SP1 HF5).

                    Stream s7h = _ziphelper.GetReadStream(filepath);
                    BinaryReader rd = new BinaryReader(s7h);
                    byte[] completeBuffer = rd.ReadBytes((int)_ziphelper.GetStreamLength(filepath, s7h));
                    rd.Close();
                    s7h.Close();

                    // CPU MLFB (ordering number) extraction algorithm:
                    // 1. Search for magic byte sequences that precede hardware slot definitions
                    // 2. Different CPU types use different byte patterns (0x0a/0x0c/0x09 + 0x00 0x03)
                    // 3. Filter matches to first 2000 bytes (MLFB appears early in file)
                    // 4. Second match typically contains CPU information (first is power supply/rack)
                    // 5. Parse three text fields with lengths stored in byte immediately before each field
                    // 6. Extract CPU MLFB (e.g., "6ES7 318-3EL00-0AB0")
                    string[] checkSequences = {
                        ASCIIEncoding.ASCII.GetString(new byte[] { 0x0a, 0x00, 0x03 }),// 319-3 (EL00/01), 317-2DP
                        ASCIIEncoding.ASCII.GetString(new byte[] { 0x0c, 0x00, 0x03 }),// 317T-3 PN/DP
                        ASCIIEncoding.ASCII.GetString(new byte[] { 0x09, 0x00, 0x03 }) // PLCType.SimaticRTX
                    };
                    string buffer = ASCIIEncoding.ASCII.GetString(completeBuffer);
                    List<IEnumerable<int>> hitsList = new List<IEnumerable<int>>();
                    foreach (string checkSeq in checkSequences)
                    {
                        //Find the indexes of the matches in the byte-buffer.
                        var matches = System.Text.RegularExpressions.Regex.
                            Matches(buffer, checkSeq).Cast<System.Text.
                            RegularExpressions.Match>().Select(m => m.Index);
                        // If the byte address i higher than 2000, it is most likely
                        // not the CPU MLFB that is found.
                        if (matches.Count() > 0 && matches.ElementAt(0) < 2000)
                        {
                            hitsList.Add(matches);
                        }
                    }
                    // Sort the list by the result with most matches.
                    hitsList.Sort((a, b) => b.Count() - a.Count());
                    // Take the result with the most matches.
                    IEnumerable<int> matchList = hitsList.First();

                    // The first hit has e.g. slot1, power supply etc.
                    // The second hit has the CPU information
                    if (matchList.Count() < 2)
                        continue;

                    //The length of the text field is the byte before the text field.
                    //First two fields are seperated by bytes "03 20 20 32"
                    int descrStart = matchList.ElementAt(1);
                    int dscr1_length = completeBuffer[descrStart + 6]; // CPU Name
                    int dscr2_length = completeBuffer[descrStart + 6 + dscr1_length + 5]; // CPU Name
                    int dscr3_length = completeBuffer[descrStart + 6 + dscr1_length + 5 + dscr2_length + 1]; // CPU MLFB
                    int dscr3_position = descrStart + 6 + dscr1_length + 5 + dscr2_length + 2;
                    string descr3 = ASCIIEncoding.ASCII.GetString(completeBuffer, dscr3_position, dscr3_length);

                    y.MLFB_OrderNumber = descr3;
                }
                catch
                {
                    Console.WriteLine("1 Step7ProjectV5.cs threw exception");
                }
            }

            // ===== SECTION 8: Parse CPU Details =====
            // Load CPU names, rack/slot positions from HOBJECT1.DBF files
            // Separate databases for different CPU types (ET200S, S7-300, S7-400)

            // Get The CPU (ET200S)
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkcomx" + _DirSeperator + "HOBJECT1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkcomx" + _DirSeperator + "HOBJECT1.DBF", _ziphelper, _DirSeperator);

                foreach (var y in CPUFolders)
                {
                    foreach (DataRow row in dbfTbl.Rows)
                    {
                        if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                        {
                            if ((int)row["ID"] == y.ID && y.CpuType == PLCType.SimaticET200)
                            //if ((int)row["UNITID"] == y.UnitID && y.CpuType == PLCType.SimaticET200)
                            {
                                y.Name = ((string)row["Name"]).Replace("\0", "");
                                if ((bool)row["DELETED_FLAG"]) y.Name = "$$_" + y.Name;
                                y.ID = (int)row["ID"];

                                y.Rack = (int)row["SUBSTATN"];
                                y.Slot = (int)row["MODULN"];
                            }
                        }
                    }
                }
            }

            //Get The CPU(300)...
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk31ax" + _DirSeperator + "HOBJECT1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk31ax" + _DirSeperator + "HOBJECT1.DBF", _ziphelper, _DirSeperator);

                foreach (var y in CPUFolders)
                {
                    foreach (DataRow row in dbfTbl.Rows)
                    {
                        if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                        {
                            if ((int)row["ID"] == y.ID &&
                                (y.CpuType == PLCType.Simatic300 ||
                                y.CpuType == PLCType.SimaticRTX))
                            //if ((int)row["UNITID"] == y.UnitID && y.CpuType == PLCType.Simatic300)
                            {
                                y.Name = ((string)row["Name"]).Replace("\0", "");
                                if ((bool)row["DELETED_FLAG"]) y.Name = "$$_" + y.Name;
                                y.ID = (int)row["ID"];

                                y.Rack = (int)row["SUBSTATN"];
                                y.Slot = (int)row["MODULN"];
                            }
                        }
                    }
                }
            }

            // ===== SECTION 9: Decrypt CPU Passwords =====
            // Extract and decrypt CPU protection passwords from HATTRME1.DBF
            // Passwords are XOR-encrypted with 0xAA in the project database
            // ATTRIIDM 111142 indicates password attribute
            // Only decrypts read/write protection passwords (not all protection levels)

            // Get The CPU (300) password
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk31ax" + _DirSeperator + "HATTRME1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk31ax" + _DirSeperator + "HATTRME1.DBF", _ziphelper, _DirSeperator);
                byte[] memoarray = null;

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"])
                    {
                        if ((int)row["ATTRIIDM"] == 111142)
                        {
                            if (row["MEMOARRAYM"] != DBNull.Value)
                                memoarray = (byte[])row["MEMOARRAYM"];

                            if (memoarray.Length >= 12)
                            {
                                // Password decryption algorithm (XOR-based):
                                // - memoarray[3] contains password level (1-3)
                                // - First 2 bytes: XOR with 0xAA
                                // - Remaining bytes: XOR with (byte at i+2) XOR (byte at i+4) XOR 0xAA
                                // - Result is 8-byte password string
                                byte[] mempass = new byte[8];
                                for (int i = 0; i < 8; i++)
                                {
                                    if (i < 2) mempass[i] = (byte)(memoarray[i + 4] ^ 0xAA);
                                    else mempass[i] = (byte)(memoarray[i + 2] ^ memoarray[i + 4] ^ 0xAA);
                                }
                                string res = ProjectEncoding.GetString(mempass);
                                foreach (var y in CPUFolders)
                                {
                                    if ((int)row["IDM"] == y.ID)
                                    {
                                        y.PasswdHard = res.Trim();
                                    }
                                }
                            }
                        }
                    }
                }
            }

            //Get The CPU(400)...
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk41ax" + _DirSeperator + "HOBJECT1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk41ax" + _DirSeperator + "HOBJECT1.DBF", _ziphelper, _DirSeperator);

                foreach (var y in CPUFolders)
                {
                    foreach (DataRow row in dbfTbl.Rows)
                    {
                        if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                        {
                            if ((int)row["ID"] == y.ID && (y.CpuType == PLCType.Simatic400 || y.CpuType == PLCType.Simatic400H))
                            //if ((int)row["UNITID"] == y.UnitID && (y.CpuType == PLCType.Simatic400 || y.CpuType == PLCType.Simatic400H) )
                            {
                                y.Name = ((string)row["Name"]).Replace("\0", "");
                                if ((bool)row["DELETED_FLAG"]) y.Name = "$$_" + y.Name;
                                y.ID = (int)row["ID"];

                                y.Rack = (int)row["SUBSTATN"];
                                y.Slot = (int)row["MODULN"];
                            }
                        }
                    }
                }
            }

            //Get The CPU(400) password
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk41ax" + _DirSeperator + "HATTRME1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk41ax" + _DirSeperator + "HATTRME1.DBF", _ziphelper, _DirSeperator);
                byte[] memoarray = null;

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"])
                    {
                        if ((int)row["ATTRIIDM"] == 111142)
                        {
                            if (row["MEMOARRAYM"] != DBNull.Value)
                                memoarray = (byte[])row["MEMOARRAYM"];
                            if (memoarray.Length >= 12)
                            {
                                // Password decryption algorithm (XOR-based):
                                // - memoarray[3] contains password level (1-3)
                                // - First 2 bytes: XOR with 0xAA
                                // - Remaining bytes: XOR with (byte at i+2) XOR (byte at i+4) XOR 0xAA
                                // - Result is 8-byte password string
                                byte[] mempass = new byte[8];
                                for (int i = 0; i < 8; i++)
                                {
                                    if (i < 2) mempass[i] = (byte)(memoarray[i + 4] ^ 0xAA);
                                    else mempass[i] = (byte)(memoarray[i + 2] ^ memoarray[i + 4] ^ 0xAA);
                                }
                                string res = ProjectEncoding.GetString(mempass);
                                foreach (var y in CPUFolders)
                                {
                                    if ((int)row["IDM"] == y.ID)
                                    {
                                        y.PasswdHard = res.Trim();
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // ===== SECTION 10: Parse Program Folders =====
            // Load S7 program folders from hrs/S7RESOFF.DBF
            // Program folders contain the actual PLC code blocks
            // RSRVD4_L field contains offset into link file for folder relationships
            var tmpS7ProgrammFolders = new List<S7ProgrammFolder>();
            if (_ziphelper.FileExists(ProjectFolder + "hrs" + _DirSeperator + "S7RESOFF.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hrs" + _DirSeperator + "S7RESOFF.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        var x = new S7ProgrammFolder() { Project = this };
                        x.Name = ((string)row["Name"]).Replace("\0", "");
                        if ((bool)row["DELETED_FLAG"]) x.Name = "$$_" + x.Name;
                        x.ID = (int)row["ID"];
                        x._linkfileoffset = (int)row["RSRVD4_L"];
                        S7ProgrammFolders.Add(x);
                        tmpS7ProgrammFolders.Add(x);
                        _allFolders.Add(x);
                    }
                }
            }

            // ===== SECTION 11: Link Programs to CPUs =====
            // Establish CPU-to-program-folder relationships using HRELATI1.DBF
            // RELID 16 indicates program folder belongs to CPU
            // Process separately for S7-300, ET200S, and S7-400 CPUs

            // Combine Folder and CPU (300)
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk31ax" + _DirSeperator + "HRELATI1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk31ax" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        if ((int)row["RELID"] == 16)
                        {
                            int cpuid = (int)row["SOBJID"];
                            int fldid = (int)row["TOBJID"];
                            foreach (var y in CPUFolders)
                            {
                                if (y.ID == cpuid &&
                                    (y.CpuType == PLCType.Simatic300 ||
                                    y.CpuType == PLCType.SimaticRTX))
                                {
                                    foreach (var z in S7ProgrammFolders)
                                    {
                                        if (z.ID == fldid)
                                        {
                                            z.Parent = y;
                                            y.SubItems.Add(z);
                                            tmpS7ProgrammFolders.Remove(z);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            //Combine Folder and CPU (300 ET200s)
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkcomx" + _DirSeperator + "HRELATI1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkcomx" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        if ((int)row["RELID"] == 16)
                        {
                            int cpuid = (int)row["SOBJID"];
                            int fldid = (int)row["TOBJID"];
                            foreach (var y in CPUFolders)
                            {
                                if (y.ID == cpuid && y.CpuType == PLCType.SimaticET200)
                                {
                                    foreach (var z in S7ProgrammFolders)
                                    {
                                        if (z.ID == fldid)
                                        {
                                            z.Parent = y;
                                            y.SubItems.Add(z);
                                            tmpS7ProgrammFolders.Remove(z);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            //Combine Folder and CPU (400)
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk41ax" + _DirSeperator + "HRELATI1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hk41ax" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        if ((int)row["RELID"] == 16)
                        {
                            int cpuid = (int)row["SOBJID"];
                            int fldid = (int)row["TOBJID"];
                            foreach (var y in CPUFolders)
                            {
                                if (y.ID == cpuid && (y.CpuType == PLCType.Simatic400 || y.CpuType == PLCType.Simatic400H))
                                {
                                    foreach (var z in S7ProgrammFolders)
                                    {
                                        if (z.ID == fldid)
                                        {
                                            z.Parent = y;
                                            y.SubItems.Add(z);
                                            tmpS7ProgrammFolders.Remove(z);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // ===== SECTION 12: Add Orphaned Programs =====
            // Add program folders not linked to any CPU to the root project structure
            // This handles edge cases and malformed projects
            foreach (var z in tmpS7ProgrammFolders)
            {
                z.Parent = ProjectStructure;
                ProjectStructure.SubItems.Add(z);
            }

            // ===== SECTION 13: Parse Symbol Tables =====
            // Load symbol tables (variable name mappings) for each program folder
            // Symbol tables map symbolic names to PLC addresses
            foreach (var z in S7ProgrammFolders)
            {
                var symtab = _GetSymTabForProject(z, this._showDeleted);
                if (symtab != null)
                {
                    symtab.Parent = z;
                    z.SymbolTable = symtab;
                    z.SubItems.Add(symtab);
                    _allFolders.Add(symtab);
                }
            }

            // ===== SECTION 14: Parse Block Folders =====
            // Create offline block folders from ombstx/offline/BSTCNTOF.DBF
            // Block folders contain compiled PLC blocks (FC, FB, DB, etc.)
            var tmpBlocksOfflineFolders = new List<BlocksOfflineFolder>();
            if (_ziphelper.FileExists(ProjectFolder + "ombstx" + _DirSeperator + "offline" + _DirSeperator + "BSTCNTOF.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "ombstx" + _DirSeperator + "offline" + _DirSeperator + "BSTCNTOF.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        var x = new BlocksOfflineFolder() { Project = this };
                        x.Name = ((string)row["Name"]).Replace("\0", "");
                        if ((bool)row["DELETED_FLAG"]) x.Name = "$$_" + x.Name;
                        x.ID = (int)row["ID"];
                        x.Folder = ProjectFolder + "ombstx" + _DirSeperator + "offline" + _DirSeperator + x.ID.ToString("X").PadLeft(8, '0') + _DirSeperator;
                        tmpBlocksOfflineFolders.Add(x);
                        _blocksOfflineFolders.Add(x);
                        _allFolders.Add(x);
                    }
                }
            }

            // ===== SECTION 15: Parse Source Folders =====
            // Create source code folders from s7asrcom/S7CNTREF.DBF
            // Source folders contain AWL/STL source files before compilation
            var Step7ProjectTypeStep7Sources = new List<SourceFolder>();
            if (_ziphelper.FileExists(ProjectFolder + "s7asrcom" + _DirSeperator + "S7CNTREF.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "s7asrcom" + _DirSeperator + "S7CNTREF.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        var x = new SourceFolder() { Project = this };
                        x.Name = ((string)row["Name"]).Replace("\0", "");
                        if ((bool)row["DELETED_FLAG"]) x.Name = "$$_" + x.Name;
                        x.ID = (int)row["ID"];
                        x.Folder = ProjectFolder + "s7asrcom" + _DirSeperator + x.ID.ToString("X").PadLeft(8, '0') + _DirSeperator;
                        Step7ProjectTypeStep7Sources.Add(x);
                        _allFolders.Add(x);
                    }
                }
            }

            // ===== SECTION 16: Parse Profibus Networks =====
            // Parse Profibus DP master systems and nodes from S7HDPSSX databases
            // Profibus is a fieldbus protocol for industrial automation
            // Links master systems to stations and enumerates connected nodes
            var pbMasterSystems = new List<ProfibusMasterSystem>();

            // Get all Profibus Master Systems
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "S7HDPSSX" + _DirSeperator + "HOBJECT1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "S7HDPSSX" + _DirSeperator + "HOBJECT1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        if ((int)row["OBJTYP"] == 1314971)
                        {
                            var x = new ProfibusMasterSystem() { Project = this, Name = row["NAME"].ToString().Replace("\0", ""), Id = (int)row["ID"] };
                            pbMasterSystems.Add(x);
                            _allFolders.Add(x);
                        }
                    }
                }
            }

            //Link all PbMasterSystems to the Stations
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "S7HDPSSX" + _DirSeperator + "HRELATI1.DBF"))
            {
                var lnkLst = new List<LinkHelp>();

                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "S7HDPSSX" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        lnkLst.Add(new LinkHelp() { SOBJID = (int)row["SOBJID"], SOBJTYP = (int)row["SOBJTYP"], RELID = (int)row["RELID"], TOBJID = (int)row["TOBJID"], TOBJTYP = (int)row["TOBJTYP"], TUNITID = (int)row["TUNITID"], TUNITTYP = (int)row["TUNITTYP"] });
                    }
                }

                foreach (StationConfigurationFolder station in stations)
                {
                    var lnks = lnkLst.Where(x => x.TOBJTYP == station.ObjTyp && x.TOBJID == station.ID);
                    foreach (LinkHelp linkHelp in lnks)
                    {
                        var ms = pbMasterSystems.FirstOrDefault(x => x.Id == linkHelp.SOBJID);
                        if (ms != null)
                        {
                            station.MasterSystems.Add(ms);
                            station.SubItems.Add(ms);
                        }
                    }
                }
            }

            //Get all Profibus Parts
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hslntx" + _DirSeperator + "HOBJECT1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hslntx" + _DirSeperator + "HOBJECT1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        if ((int)row["OBJTYP"] == 1314988)
                        {
                            var node = new ProfibusNode() { Name = row["NAME"].ToString().Replace("\0", ""), NodeId = (int)row["SUBSTATN"], GsdFile = row["CEXTTYPE"].ToString() };

                            var ma = pbMasterSystems.FirstOrDefault(x => x.Id == (int)row["UNITID"]);
                            if (ma != null)
                                ma.Children.Add(node);
                        }
                    }
                }
            }

            // ===== SECTION 17: Parse Profinet Networks =====
            // Parse Profinet IO master systems and devices from s7hssiox databases
            // Profinet is industrial Ethernet protocol
            // Links Ethernet interfaces in CPUs and CPs to network objects
            var pnMasterSystems = new List<ProfinetMasterSystem>();

            // Get all Profinet Master Systems
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hssiox" + _DirSeperator + "HOBJECT1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hssiox" + _DirSeperator + "HOBJECT1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        int objType = Convert.ToInt32(row["OBJTYP"]);
                        if (objType == 1316787)
                        {
                            var x = new ProfinetMasterSystem() { Project = this, Name = row["NAME"].ToString().Replace("\0", ""), Id = (int)row["ID"] };
                            pnMasterSystems.Add(x);
                            _allFolders.Add(x);
                        }

                        // Store Ethernet interface hardware object IDs for CPUs
                        // These IDs are used in next section to map to network configuration objects
                        // Matches various Ethernet interface types integrated in CPUs
                        else if (objType == objectType_EternetInCPU3xxF ||
                            objType == objectType_EternetInCPU3xx ||
                            objType == objectType_EternetInCPU4xx ||
                            objType == objectType_EternetInCPU3xx_2 ||
                            objType == objectType_EternetInCPURTX ||
                            objType == objectType_Eternet319 ||
                            objType == objectType_EternetPNIO)
                        {
                            var cpu = CPUFolders.FirstOrDefault(x => x.ID == Convert.ToInt32(row["UNITID"]));

                            if (cpu != null)
                            {
                                cpu.IdTobjId = Convert.ToInt32(row["ID"]);  // Store Ethernet interface hardware ID
                            }
                            else
                            {
                                Console.WriteLine($"Step7ProjectV5.cs: Found Ethernet interface (ID={row["ID"]}) but no matching CPU with ID={row["UNITID"]}");
                            }
                        }
                        // Store Ethernet interface hardware object IDs for Communication Processors (CPs)
                        // CPs can have multiple Ethernet interfaces, so IdTobjId is a list
                        // Object types: 2364971 (CP343-1), 2367589 (CP443-1)
                        else if (objType == 2364971 || objType == 2367589)
                        {
                            var cp = CPFolders.FirstOrDefault(x => x.ID == (int)row["UNITID"]);
                            if (cp != null)
                            {
                                if (cp.IdTobjId == null) cp.IdTobjId = new List<int>();
                                cp.IdTobjId.Add((int)row["ID"]);  // Store Ethernet interface hardware ID
                            }
                            else
                            {
                                Console.WriteLine($"Step7ProjectV5.cs: Found Ethernet interface (ID={row["ID"]}) but no matching CP with ID={row["UNITID"]}");
                            }
                        }
                    }
                }
            }

            // ===== Map Ethernet Interface IDs to Network Configuration Object IDs =====
            // This critical step links Ethernet hardware interfaces (IdTobjId) to their network config objects (TobjId)
            // The TobjId is later used in SECTION 20 to match network configurations from S7NONFGX.tab
            //
            // Data flow:
            // 1. Previous section set: cpu.IdTobjId = Ethernet interface hardware ID (from s7hssiox/HOBJECT1.DBF)
            // 2. This section reads: s7hssiox/HRELATI1.DBF with RELID=64 (network interface reference)
            //    - SOBJID = Ethernet interface hardware ID (matches IdTobjId)
            //    - TOBJID = Network configuration object ID
            // 3. This section sets: cpu.TobjId = TOBJID (network config object ID for SECTION 20)
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hssiox" + _DirSeperator + "HRELATI1.DBF"))
            {
                var lnkLst = new List<LinkHelp>();

                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hssiox" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        // RELID 64 = Network interface reference relationship
                        // Links Ethernet interface hardware object (SOBJID) to network config object (TOBJID)
                        if (Convert.ToInt32(row["RELID"]) == 64)
                        {
                            int sobjId = Convert.ToInt32(row["SOBJID"]);  // Ethernet interface hardware ID
                            int tobjId = Convert.ToInt32(row["TOBJID"]);  // Network configuration object ID

                            // Find CPU by Ethernet interface ID (set in previous section from HOBJECT1.DBF)
                            var cpu = CPUFolders.FirstOrDefault(x => x.IdTobjId == sobjId);
                            if (cpu != null)
                            {
                                cpu.TobjId = tobjId;  // Store network config object ID for SECTION 20
                            }

                            // Find CP by Ethernet interface ID (CPs can have multiple interfaces)
                            var cp = CPFolders.FirstOrDefault(x => x.IdTobjId != null && x.IdTobjId.Any(c => c == sobjId));
                            if (cp != null)
                            {
                                if (cp.TobjId == null) cp.TobjId = new List<int>();
                                cp.TobjId.Add(tobjId);  // Store network config object ID for SECTION 20
                            }

                            // Debug: Log if interface ID couldn't be mapped to any CPU/CP
                            if (cpu == null && cp == null)
                            {
                                Console.WriteLine($"Step7ProjectV5.cs: RELID 64 - Could not map interface ID {sobjId} to network config {tobjId} (no matching CPU/CP with IdTobjId={sobjId})");
                            }
                        }

                        // Store all relationships for linking Profinet Master Systems to Stations (next step)
                        lnkLst.Add(new LinkHelp() { SOBJID = (int)row["SOBJID"], SOBJTYP = (int)row["SOBJTYP"], RELID = (int)row["RELID"], TOBJID = (int)row["TOBJID"], TOBJTYP = (int)row["TOBJTYP"], TUNITID = (int)row["TUNITID"], TUNITTYP = (int)row["TUNITTYP"] });
                    }
                }

                // ===== Link Profinet Master Systems to their Parent Stations =====
                // Uses relationship data to establish parent-child hierarchy
                // ProfinetMasterSystem (SOBJID) → belongs to → Station (TOBJID)
                foreach (StationConfigurationFolder station in stations)
                {
                    // Find all relationships where station is the target (TOBJID matches station ID and type)
                    var lnks = lnkLst.Where(x => x.TOBJTYP == station.ObjTyp && x.TOBJID == station.ID);
                    foreach (LinkHelp linkHelp in lnks)
                    {
                        // Find Profinet Master System by source object ID
                        var ms = pnMasterSystems.FirstOrDefault(x => x.Id == linkHelp.SOBJID);
                        if (ms != null)
                        {
                            station.MasterSystems.Add(ms);
                            station.SubItems.Add(ms);
                        }
                    }
                }
            }

            //Get all Profinet Parts ...
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hdevnx" + _DirSeperator + "HOBJECT1.DBF"))
            {
                var attLst = new List<AttrMeHelp>();

                //Read real name from hattrme.dbf
                if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hdevnx" + _DirSeperator + "HATTRME1.DBF"))
                {
                    var dbfTbl2 = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hdevnx" + _DirSeperator + "HATTRME1.DBF", _ziphelper, _DirSeperator);

                    foreach (DataRow row in dbfTbl2.Rows)
                    {
                        if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                        {
                            attLst.Add(new AttrMeHelp() { IDM = (int)row["IDM"], ATTRIIDM = (int)row["ATTRIIDM"], ATTFORMATM = (int)row["ATTFORMATM"], MEMOARRAYM = (byte[])row["MEMOARRAYM"] });
                        }
                    }
                }

                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hdevnx" + _DirSeperator + "HOBJECT1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        if ((int)row["OBJTYP"] == 1316803)
                        {
                            var node = new ProfibusNode() { Name = row["NAME"].ToString().Replace("\0", ""), NodeId = (int)row["SUBSTATN"], GsdFile = row["CEXTTYPE"].ToString() };

                            var inf = attLst.FirstOrDefault(x => x.IDM == (int)row["ID"] && x.ATTRIIDM == 111386);

                            if (inf != null)
                                node.Name = ProjectEncoding.GetString(inf.MEMOARRAYM).Replace("\0", "");

                            var ma = pnMasterSystems.FirstOrDefault(x => x.Id == (int)row["UNITID"]);
                            if (ma != null)
                                ma.Children.Add(node);
                        }
                    }
                }
            }

            // ===== SECTION 18: Link Programs to Blocks via Link File =====
            // Parse hrs/linkhrs.lnk binary file to establish program-to-block-folder relationships
            // Link file contains 512-byte structures with folder ID mappings
            // Offset of link structure is in hrs\S7RESOFF.DBF, Field RSRVD4_L
            // Byte pattern 0x01,0x60,0x11,0x00 precedes block folder ID (2 bytes)
            // Byte pattern 0x04,0x20,0x11 precedes source folder ID (2 bytes)
            if (_ziphelper.FileExists(ProjectFolder + "hrs" + _DirSeperator + "linkhrs.lnk"))
            {
                //FileStream hrsLink = new FileStream(ProjectFolder + "hrs" + _DirSeperator + "linkhrs.lnk", FileMode.Open, FileAccess.Read, System.IO.FileShare.ReadWrite);
                Stream hrsLink = _ziphelper.GetReadStream(ProjectFolder + "hrs" + _DirSeperator + "linkhrs.lnk");
                BinaryReader rd = new BinaryReader(hrsLink);
                byte[] completeBuffer = rd.ReadBytes((int)_ziphelper.GetStreamLength(ProjectFolder + "hrs" + _DirSeperator + "linkhrs.lnk", hrsLink));
                rd.Close();
                hrsLink.Close();
                hrsLink = new MemoryStream(completeBuffer);

                foreach (var x in S7ProgrammFolders)
                {
                    byte[] tmpLink = new byte[0x200];
                    hrsLink.Position = x._linkfileoffset;
                    hrsLink.Read(tmpLink, 0, 0x200);

                    int pos1 = ASCIIEncoding.ASCII.GetString(tmpLink).IndexOf(ASCIIEncoding.ASCII.GetString(new byte[] { 0x01, 0x60, 0x11, 0x00 }));
                    int wrt1 = BitConverter.ToInt16(tmpLink, pos1 + 3);

                    int pos2 = tmpLink.IndexOfBytes(new byte[] { 0x04, 0x20, 0x11 });
                    int wrt2 = tmpLink[pos2 + 3] * 0x100 + tmpLink[pos2 + 4];

                    BlocksOfflineFolder fld = null;
                    foreach (var y in tmpBlocksOfflineFolders)
                    {
                        if (y.ID == wrt1)
                        {
                            y.Parent = x;
                            x.SubItems.Add(y);
                            x.BlocksOfflineFolder = y;
                            fld = y;
                            break;
                        }
                    }

                    if (fld != null)
                        tmpBlocksOfflineFolders.Remove(fld);

                    foreach (var y in Step7ProjectTypeStep7Sources)
                    {
                        if (y.ID == wrt2)
                        {
                            y.Parent = x;
                            x.SubItems.Add(y);
                            x.SourceFolder = y;
                        }
                    }
                }
                hrsLink.Close();
            }
            else
            {
                foreach (var y in tmpBlocksOfflineFolders)
                {
                    y.Parent = ProjectStructure;
                    ProjectStructure.SubItems.Add(y);
                }

                foreach (var y in Step7ProjectTypeStep7Sources)
                {
                    y.Parent = ProjectStructure;
                    ProjectStructure.SubItems.Add(y);
                }
            }

            if (_showDeleted)
            {
                foreach (var y in tmpBlocksOfflineFolders)
                {
                    var x = new S7ProgrammFolder() { Name = "$$tmpProgram_for_deleted" };
                    x.Project = this;
                    x.Parent = ProjectStructure;
                    y.Parent = x;
                    x.SubItems.Add(y);
                    x.BlocksOfflineFolder = y;
                    ProjectStructure.SubItems.Add(x);
                }
            }

            // ===== SECTION 19: Parse MPI/DP Hardware =====
            // Load MPI/Profibus interface configurations from s7hkdmax databases
            // Creates DpHelp objects for MPI/Profibus interfaces
            // RELID 1315837 links to hardware, RELID 64 provides address information
            List<DpHelp> DPlist = new List<DpHelp>();
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkdmax" + _DirSeperator + "HOBJECT1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkdmax" + _DirSeperator + "HOBJECT1.DBF", _ziphelper, _DirSeperator);

                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || _showDeleted)
                    {
                        var dp = new DpHelp();
                        dp.id = Convert.ToInt32(row["ID"]);
                        DPlist.Add(dp);
                    }
                }
            }
            if (_ziphelper.FileExists(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkdmax" + _DirSeperator + "HRELATI1.DBF"))
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "hOmSave7" + _DirSeperator + "s7hkdmax" + _DirSeperator + "HRELATI1.DBF", _ziphelper, _DirSeperator);

                var rrr = DPFolders;
                foreach (DataRow row in dbfTbl.Rows)
                {
                    int relID = Convert.ToInt32(row["RELID"]);
                    if (relID == 1315837)
                    {
                        var dp = DPlist.FirstOrDefault(x => x.id == Convert.ToInt32(row["SOBJID"]));
                        if (dp != null)
                        {
                            dp.TobjID = Convert.ToInt32(row["TOBJID"]);
                        }
                    }
                    else if (relID == 64)
                    {
                        var dp = DPlist.FirstOrDefault(x => x.id == Convert.ToInt32(row["SOBJID"]));
                        if (dp != null)
                        {
                            dp.addr = Convert.ToInt32(row["TOBJID"]);
                        }
                    }
                }
            }
            //remove DP from DPlist to DPFolder
            foreach (var dp in DPlist)
            {
                var dpf = DPFolders.FirstOrDefault(x => x.IdTobjId != null && x.IdTobjId.Any(y => y == dp.TobjID));
                if (dpf != null)
                {
                    if (dpf.TobjId == null) dpf.TobjId = new List<int>();
                    dpf.TobjId.Add(dp.addr);
                }
            }
            //add subitem to parent
            //foreach (var cp in CPFolders.Where(x => x.SubModulNumber > 0))
            //{
            //    var parent = CPFolders.FirstOrDefault(x => x.ID == cp.UnitID);
            //    if (parent != null) parent.SubModul = cp;
            //}

            // ===== SECTION 20: Parse Network Configuration =====
            // Parse S7Netze/S7NONFGX.tab for detailed network interface configurations
            // Extracts IP addresses, MAC addresses, subnet masks, router settings
            // Processes Ethernet, Profibus DP, and MPI interface parameters
            // Uses binary pattern matching to locate configuration structures
            try
            {
                // === ETHERNET NETWORK CONFIGURATION ===
                // Parse S7Netze/S7NONFGX.tab binary file containing Ethernet interface configurations
                // This file stores all network parameters for each Ethernet connection configured in the project
                if (_ziphelper.FileExists(ProjectFolder + "S7Netze" + _DirSeperator + "S7NONFGX.tab"))
                {
                    Stream hrsLink = _ziphelper.GetReadStream(ProjectFolder + "S7Netze" + _DirSeperator + "S7NONFGX.tab");
                    BinaryReader rd = new BinaryReader(hrsLink);
                    int lengthFile = (int)_ziphelper.GetStreamLength(ProjectFolder + "S7Netze" + _DirSeperator + "S7NONFGX.tab", hrsLink);
                    byte[] completeBuffer = rd.ReadBytes(lengthFile);
                    rd.Close();
                    hrsLink.Close();

                    // S7NONFGX.tab Binary Structure Overview:
                    // ======================================
                    // The file contains repeating structures, one per Ethernet interface (~1705 bytes each)
                    //
                    // Each interface structure contains:
                    //   1. Structure Header: 0x03,0x52,0x14,0x00 + 4-byte object ID (TobjId)
                    //   2. Multiple Attribute Blocks (IP, MAC, Mask, Router, etc.)
                    //
                    // Attribute Block Format:
                    //   - 8-byte marker/header (identifies attribute type)
                    //   - Metadata bytes
                    //   - 1-byte length indicator at offset +19
                    //   - Variable-length data at offset +20 (typically hex-encoded ASCII)
                    //   - Note: Some attributes also have raw binary data at offset +12 to +15
                    //
                    // Example: IP Address Attribute
                    //   Offset +0 to +7:   0xE0,0x0F,0x00,0x00,0xE0,0x0F,0x00,0x00 (marker)
                    //   Offset +12 to +15: C0 A8 00 01 (raw IP bytes: 192.168.0.1)
                    //   Offset +19:        08 (length = 8 bytes)
                    //   Offset +20 to +27: "C0A80001" (hex-encoded ASCII string)
                    //
                    // Mapping to PLC:
                    //   - Object ID at structure start links to CPU.TobjId or CP.TobjId
                    //   - If direct mapping fails, fallback uses network interface link pattern

                    // Search patterns for text attributes
                    char[] searchValid = { 'A', 'd', 'd', 'r', 'e', 's', 's', 'I', 's', 'V', 'a', 'l', 'i', 'd' };
                    byte[] searchName = { (byte)'B', (byte)'a', (byte)'u', (byte)'g', (byte)'r', (byte)'u', (byte)'p', (byte)'p', (byte)'e', (byte)'n', (byte)'n', (byte)'a', (byte)'m', (byte)'e' };

                    byte[] startStructure = { 0x03, 0x52, 0x14, 0x00 };
                    byte[] startIP = { 0xE0, 0x0F, 0x00, 0x00, 0xE0, 0x0F, 0x00, 0x00 };
                    byte[] startMAC = { 0xA2, 0x0F, 0x00, 0x00, 0xA2, 0x0F, 0x00, 0x00 };
                    byte[] startMask = { 0xE5, 0x0F, 0x00, 0x00, 0xE5, 0x0F, 0x00, 0x00 };
                    byte[] startRouter = { 0xE3, 0x0F, 0x00, 0x00, 0xE3, 0x0F, 0x00, 0x00 };
                    byte[] startUseRouter = { 0xE8, 0x0F, 0x00, 0x00, 0xE8, 0x0F, 0x00, 0x00 };      // "Use Router" boolean flag marker
                    byte[] startUseIP = { 0xEA, 0x0F, 0x00, 0x00, 0xEA, 0x0F, 0x00, 0x00 };          // "Use IP" protocol boolean flag marker
                    byte[] startUseMac = { 0xED, 0x0F, 0x00, 0x00, 0xED, 0x0F, 0x00, 0x00 };         // "Use MAC" boolean flag marker
                    byte[] startNetworkInterfaceLink = { 0x10, 0x14, 0x00 };                         // Network interface link pattern (fallback mapping)

                    int position = 0;
                    int lenStructure = 1705; // Approximate length of each Ethernet interface structure (~1705 bytes, may vary)
                    while ((position = indexOfByteArray(completeBuffer, startStructure, position + 1, lengthFile)) >= 0)
                    {
                        // Extract object ID from structure header (4 bytes after startStructure marker)
                        int number = BitConverter.ToInt32(completeBuffer, position + 4);

                        // Map interface to PLC using object ID (TobjId)
                        var cp = CPFolders.FirstOrDefault(x => x.TobjId != null && x.TobjId.Any(y => y == number));
                        var cpu = CPUFolders.FirstOrDefault(x => x.TobjId == number);
                        EthernetNetworkInterface ethernet = new EthernetNetworkInterface();
                        if (cp != null)
                        {
                            if (cp.NetworkInterfaces == null) cp.NetworkInterfaces = new List<NetworkInterface>();
                            cp.NetworkInterfaces.Add(ethernet);
                        }
                        else if (cpu != null)
                        {
                            if (cpu.NetworkInterfaces == null) cpu.NetworkInterfaces = new List<NetworkInterface>();
                            cpu.NetworkInterfaces.Add(ethernet);
                        }

                        int pos = indexOfByteArray(completeBuffer, searchName, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                string strName = System.Text.Encoding.Default.GetString(completeBuffer, pos + 25, (int)completeBuffer[pos + 24]);

                                ethernet.Name = strName;
                            }
                            catch
                            {
                                Console.WriteLine("2 Step7ProjectV5.cs threw exception");
                            }
                        }

                        // Extract IP Address from binary structure
                        // Binary layout after startIP marker (0xE0 0x0F 0x00 0x00 0xE0 0x0F 0x00 0x00):
                        //   Offset +0 to +7:   startIP pattern (marker bytes)
                        //   Offset +8 to +11:  Metadata/padding
                        //   Offset +12 to +15: IP address as 4 raw bytes (e.g., C0 A8 00 01 = 192.168.0.1)
                        //   Offset +16 to +18: Additional metadata
                        //   Offset +19:        Length of hex string (typically 8)
                        //   Offset +20 to +X:  IP as hex-encoded ASCII string (e.g., "C0A80001" = 192.168.0.1)
                        pos = indexOfByteArray(completeBuffer, startIP, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                // Read IP as hex-encoded ASCII string from offset +20
                                // Length stored in byte at offset +19 (typically 8 bytes for IPv4)
                                string strIP = System.Text.Encoding.Default.GetString(completeBuffer, pos + 20, (int)completeBuffer[pos + 19]);

                                // Parse hex string to IP octets: "C0A80001" → [192, 168, 0, 1]
                                byte[] bIP = new byte[4];
                                bIP[0] = byte.Parse(strIP.Substring(0, 2), System.Globalization.NumberStyles.AllowHexSpecifier); // First octet
                                bIP[1] = byte.Parse(strIP.Substring(2, 2), System.Globalization.NumberStyles.AllowHexSpecifier); // Second octet
                                bIP[2] = byte.Parse(strIP.Substring(4, 2), System.Globalization.NumberStyles.AllowHexSpecifier); // Third octet
                                bIP[3] = byte.Parse(strIP.Substring(6, 2), System.Globalization.NumberStyles.AllowHexSpecifier); // Fourth octet

                                ethernet.IpAddress = new System.Net.IPAddress(bIP);
                            }
                            catch
                            {
                                Console.WriteLine("3 Step7ProjectV5.cs threw exception");
                            }
                        }
                        // Extract MAC Address from binary structure
                        // Binary layout same as IP: [8-byte marker] + [metadata] + [length at +19] + [hex string at +20]
                        // MAC stored as hex string without separators (e.g., "0800200A0B0C")
                        pos = indexOfByteArray(completeBuffer, startMAC, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                // Read MAC as hex-encoded ASCII string (12 characters = 6 bytes)
                                string strMAC = System.Text.Encoding.Default.GetString(completeBuffer, pos + 20, (int)completeBuffer[pos + 19]);

                                // Parse hex string to MAC address: "0800200A0B0C" → 08:00:20:0A:0B:0C
                                ethernet.Mac = System.Net.NetworkInformation.PhysicalAddress.Parse(strMAC);
                            }
                            catch
                            {
                                Console.WriteLine("4 Step7ProjectV5.cs threw exception");
                            }
                        }
                        // Extract Subnet Mask from binary structure
                        // Binary layout same as IP: hex-encoded string at offset +20
                        pos = indexOfByteArray(completeBuffer, startMask, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                // Read subnet mask as hex string (e.g., "FFFFFF00" = 255.255.255.0)
                                string strMask = System.Text.Encoding.Default.GetString(completeBuffer, pos + 20, (int)completeBuffer[pos + 19]);

                                // Parse hex string to subnet mask octets
                                byte[] bIP = new byte[4];
                                bIP[0] = byte.Parse(strMask.Substring(0, 2), System.Globalization.NumberStyles.AllowHexSpecifier);
                                bIP[1] = byte.Parse(strMask.Substring(2, 2), System.Globalization.NumberStyles.AllowHexSpecifier);
                                bIP[2] = byte.Parse(strMask.Substring(4, 2), System.Globalization.NumberStyles.AllowHexSpecifier);
                                bIP[3] = byte.Parse(strMask.Substring(6, 2), System.Globalization.NumberStyles.AllowHexSpecifier);

                                ethernet.SubnetMask = new System.Net.IPAddress(bIP);
                            }
                            catch
                            {
                                Console.WriteLine("5 Step7ProjectV5.cs threw exception");
                            }
                        }

                        // Extract Default Router/Gateway address from binary structure
                        // Binary layout same as IP: hex-encoded string at offset +20
                        pos = indexOfByteArray(completeBuffer, startRouter, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                // Read router IP as hex string (e.g., "C0A80001" = 192.168.0.1)
                                string strRouter = System.Text.Encoding.Default.GetString(completeBuffer, pos + 20, (int)completeBuffer[pos + 19]);

                                // Parse hex string to router IP octets
                                byte[] bIP = new byte[4];
                                bIP[0] = byte.Parse(strRouter.Substring(0, 2), System.Globalization.NumberStyles.AllowHexSpecifier);
                                bIP[1] = byte.Parse(strRouter.Substring(2, 2), System.Globalization.NumberStyles.AllowHexSpecifier);
                                bIP[2] = byte.Parse(strRouter.Substring(4, 2), System.Globalization.NumberStyles.AllowHexSpecifier);
                                bIP[3] = byte.Parse(strRouter.Substring(6, 2), System.Globalization.NumberStyles.AllowHexSpecifier);

                                ethernet.Router = new System.Net.IPAddress(bIP);
                            }
                            catch
                            {
                                Console.WriteLine("6 Step7ProjectV5.cs threw exception");
                            }
                        }
                        // Extract UseRouter flag (boolean indicating if router/gateway is used)
                        // Boolean stored as single byte at offset +19
                        pos = indexOfByteArray(completeBuffer, startUseRouter, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                ethernet.UseRouter = Convert.ToBoolean(completeBuffer[pos + 19]);
                            }
                            catch
                            {
                                Console.WriteLine("7 Step7ProjectV5.cs threw exception");
                            }
                        }

                        // Extract UseIP flag (boolean indicating if IP protocol is enabled)
                        // Boolean stored as single byte at offset +19
                        pos = indexOfByteArray(completeBuffer, startUseIP, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                ethernet.UseIp = Convert.ToBoolean(completeBuffer[pos + 19]);
                            }
                            catch
                            {
                                Console.WriteLine("8 Step7ProjectV5.cs threw exception");
                            }
                        }
                        pos = indexOfByteArray(completeBuffer, startUseMac, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                ethernet.UseIso = Convert.ToBoolean(completeBuffer[pos + 19]);
                            }
                            catch
                            {
                                Console.WriteLine("9 Step7ProjectV5.cs threw exception");
                            }
                        }
                    }
                    //read ProfiBus Parametrs
                    startStructure[0] = 0x02;
                    byte[] startAddress = { 0x36, 0x08, 0x00, 0x00, 0x36, 0x08, 0x00, 0x00 };
                    lenStructure = 2000;
                    position = 0;
                    while ((position = indexOfByteArray(completeBuffer, startStructure, position + 1, lengthFile)) >= 0)
                    {
                        int number = BitConverter.ToInt32(completeBuffer, position + 4);//or ToInt16
                        var dp = DPFolders.FirstOrDefault(x => x.TobjId != null && x.TobjId.Any(y => y == number));
                        CPUFolder cpu = null;
                        if (dp != null)
                            cpu = CPUFolders.FirstOrDefault(x => x.UnitID == dp.UnitID);
                        MpiProfiBusNetworkInterface MpiDP = new MpiProfiBusNetworkInterface() { NetworkInterfaceType = NetworkType.Profibus };
                        if (cpu != null)
                        {
                            if (cpu.NetworkInterfaces == null) cpu.NetworkInterfaces = new List<NetworkInterface>();
                            cpu.NetworkInterfaces.Add(MpiDP);
                        }
                        else continue;

                        int pos = indexOfByteArray(completeBuffer, startAddress, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                MpiDP.Address = (int)Convert.ToByte(completeBuffer[pos + 19 + (int)Convert.ToByte(completeBuffer[pos + 8])]);
                            }
                            catch
                            {
                                Console.WriteLine("10 Step7ProjectV5.cs threw exception");
                            }
                        }
                        pos = indexOfByteArray(completeBuffer, searchName, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                string strName = System.Text.Encoding.Default.GetString(completeBuffer, pos + 25, (int)completeBuffer[pos + 24]);

                                MpiDP.Name = strName;
                            }
                            catch
                            {
                                Console.WriteLine("11 Step7ProjectV5.cs threw exception");
                            }
                        }
                    }
                    //read MPI Parametrs
                    startStructure[0] = 0x01;
                    byte[] startMPIAddress = { 0x9A, 0x08, 0x00, 0x00, 0x9A, 0x08, 0x00, 0x00 };
                    lenStructure = 2000;
                    position = 0;
                    while ((position = indexOfByteArray(completeBuffer, startStructure, position + 1, lengthFile)) >= 0)
                    {
                        int number = BitConverter.ToInt32(completeBuffer, position + 4);//or ToInt16
                        var dp = DPFolders.FirstOrDefault(x => x.TobjId != null && x.TobjId.Any(y => y == number));
                        CPUFolder cpu = null;
                        if (dp != null)
                            cpu = CPUFolders.FirstOrDefault(x => x.UnitID == dp.UnitID);

                        MpiProfiBusNetworkInterface MpiDP = new MpiProfiBusNetworkInterface() { NetworkInterfaceType = NetworkType.Mpi };
                        if (cpu != null)
                        {
                            if (cpu.NetworkInterfaces == null) cpu.NetworkInterfaces = new List<NetworkInterface>();
                            cpu.NetworkInterfaces.Add(MpiDP);
                        }
                        else continue;

                        int pos = indexOfByteArray(completeBuffer, startMPIAddress, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                MpiDP.Address = (int)Convert.ToByte(completeBuffer[pos + 19 + (int)Convert.ToByte(completeBuffer[pos + 8])]);
                            }
                            catch
                            {
                                Console.WriteLine("12 Step7ProjectV5.cs threw exception");
                            }
                        }
                        pos = indexOfByteArray(completeBuffer, searchName, position, lenStructure);
                        if (pos > 0)
                        {
                            try
                            {
                                string strName = System.Text.Encoding.Default.GetString(completeBuffer, pos + 25, (int)completeBuffer[pos + 24]);

                                MpiDP.Name = strName;
                            }
                            catch
                            {
                                Console.WriteLine("13 Step7ProjectV5.cs threw exception");
                            }
                        }
                    }
                }
            }
            catch
            {
                Console.WriteLine("14 Step7ProjectV5.cs threw exception");
            }

            // ===== SECTION 21: Merge Sub-Module Network Interfaces =====
            // Consolidate network interfaces from CP sub-modules into parent CP
            // Some CPs have separate interface modules that must be merged
            bool repeat;
            do
            {
                repeat = false;
                foreach (var cp in CPFolders.Where(x => x.SubModul != null))
                {
                    if (cp.NetworkInterfaces == null) cp.NetworkInterfaces = new List<NetworkInterface>();

                    if (cp.SubModul.NetworkInterfaces != null)
                    {
                        cp.NetworkInterfaces.AddRange(cp.SubModul.NetworkInterfaces);
                    }
                    CPFolders.Remove(cp.SubModul);
                    cp.SubModul = null;
                    repeat = true;
                    break;
                }
            } while (repeat);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Searches for a byte pattern within a byte array, with maximum search length limit.
        /// Used extensively for binary file parsing to locate configuration structures.
        /// </summary>
        /// <param name="array">Byte array to search within</param>
        /// <param name="pattern">Byte pattern to find</param>
        /// <param name="offset">Starting position for search</param>
        /// <param name="maxLen">Maximum number of bytes to search from offset</param>
        /// <returns>Zero-based index of first match, or -1 if not found</returns>
        private int indexOfByteArray(byte[] array, byte[] pattern, int offset, int maxLen)
        {
            int success = 0;
            int length = array.Length;
            for (int i = offset; i < length; i++)
            {
                if (array[i] == pattern[success])
                    success++;
                else if (success > 0)
                {
                    i--;
                    maxLen++;
                    success = 0;
                }
                if (pattern.Length == success)
                    return i - pattern.Length + 1;
                if (--maxLen == 0) return -1;
            }
            return -1;
        }

        /// <summary>
        /// Retrieves the symbol table associated with a program folder.
        /// Symbol tables map symbolic variable names to PLC memory addresses.
        /// </summary>
        /// <param name="myBlockFolder">Program folder to get symbol table for</param>
        /// <param name="showDeleted">If true, includes deleted symbol tables</param>
        /// <returns>SymbolTable object, or null if no symbol table exists for this program</returns>
        private SymbolTable _GetSymTabForProject(S7ProgrammFolder myBlockFolder, bool showDeleted)
        {
            var retVal = new SymbolTable() { Project = this };

            int tmpId2 = 0;

            //Look in Sym-LinkList for ID
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "YDBs" + _DirSeperator + "YLNKLIST.DBF", _ziphelper, _DirSeperator);
                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"])
                    {
                        if ((int)row["TOI"] == myBlockFolder.ID)
                        {
                            tmpId2 = (int)row["SOI"];
                            break;
                        }
                    }
                }

                if (tmpId2 == 0 && showDeleted)
                    foreach (DataRow row in dbfTbl.Rows)
                    {
                        if ((int)row["TOI"] == myBlockFolder.ID)
                        {
                            tmpId2 = (int)row["SOI"];
                            retVal.Folder = ProjectFolder + "YDBs" + _DirSeperator + tmpId2.ToString() + _DirSeperator;
                            break;
                        }
                    }
            }

            var dbPath = tmpId2.ToString();
            //Look fro Symlist Name
            {
                var dbfTbl = DBF.ParseDBF.ReadDBF(ProjectFolder + "YDBs" + _DirSeperator + "SYMLISTS.DBF", _ziphelper, _DirSeperator);
                foreach (DataRow row in dbfTbl.Rows)
                {
                    if (!(bool)row["DELETED_FLAG"] || showDeleted)
                    {
                        if ((int)row["_ID"] == tmpId2)
                        {
                            retVal.Name = (string)row["_UName"];
                            dbPath = (string)row["_DbPath"];
                            if ((bool)row["DELETED_FLAG"]) retVal.Name = "$$_" + retVal.Name;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(retVal.Name) && !File.Exists(ProjectFolder + "YDBs" + _DirSeperator + dbPath + _DirSeperator + "SYMLIST.DBF"))
                return null;

            retVal.showDeleted = showDeleted;
            if (tmpId2 != 0)
                retVal.Folder = ProjectFolder + "YDBs" + _DirSeperator + dbPath + _DirSeperator;

            return retVal;
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Helper class to store relationship data from HRELATI1.DBF files.
        /// HRELATI1 tables define parent-child relationships between hardware objects.
        /// Field names match the DBF column names for direct mapping.
        /// </summary>
        private class LinkHelp
        {
            /// <summary>Source object ID</summary>
            public int SOBJID { get; set; }

            /// <summary>Source object type</summary>
            public int SOBJTYP { get; set; }

            /// <summary>Relationship type identifier</summary>
            public int RELID { get; set; }

            /// <summary>Target object ID</summary>
            public int TOBJID { get; set; }

            /// <summary>Target object type</summary>
            public int TOBJTYP { get; set; }

            /// <summary>Target unit ID</summary>
            public int TUNITID { get; set; }

            /// <summary>Target unit type</summary>
            public int TUNITTYP { get; set; }
        }

        /// <summary>
        /// Helper class to store attribute data from HATTRME1.DBF files.
        /// HATTRME tables contain extended attributes for hardware objects (e.g., Profinet device names).
        /// </summary>
        private class AttrMeHelp
        {
            /// <summary>Object ID this attribute belongs to</summary>
            public int IDM { get; set; }

            /// <summary>Attribute type identifier</summary>
            public int ATTRIIDM { get; set; }

            /// <summary>Attribute data format</summary>
            public int ATTFORMATM { get; set; }

            /// <summary>Attribute value as byte array</summary>
            public byte[] MEMOARRAYM { get; set; }
        }

        /// <summary>
        /// Helper class to temporarily store Profibus DP interface mappings during parsing.
        /// Links DP interface IDs to their addresses and parent objects.
        /// </summary>
        private class DpHelp
        {
            /// <summary>DP interface object ID</summary>
            public int id;

            /// <summary>DP bus address</summary>
            public int addr;

            /// <summary>Target object ID (network reference)</summary>
            public int TobjID;
        }

        #endregion
    }
}