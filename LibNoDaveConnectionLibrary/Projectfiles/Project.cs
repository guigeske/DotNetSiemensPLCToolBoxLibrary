using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DotNetSiemensPLCToolBoxLibrary.DataTypes;
using DotNetSiemensPLCToolBoxLibrary.DataTypes.Projectfolders;
using NLog;

namespace DotNetSiemensPLCToolBoxLibrary.Projectfiles
{
    [Serializable]
    public abstract class Project
    {
        internal static Logger logger = LogManager.GetCurrentClassLogger();
        public abstract ProjectType ProjectType { get; }

        public string ProjectFile { get; set; }

        public string ProjectFolder { get; set; }

        public String ProjectName { get; set; }

        public String ProjectDescription { get; set; }

        private ProjectFolder _ProjectStructure;

        protected List<ProjectFolder> _allFolders = new List<ProjectFolder>();

        public List<ProjectFolder> AllFolders
        {
            get { return _allFolders; }
        }

        public MnemonicLanguage ProjectLanguage { get; set; }

        public Encoding ProjectEncoding = Encoding.GetEncoding("ISO-8859-1");

        public ProjectFolder ProjectStructure
        {
            get
            {
                if (!_projectLoaded)
                    LoadProject();
                return _ProjectStructure;
            }
            set { _ProjectStructure = value; }
        }

        protected bool _projectLoaded;

        protected abstract void LoadProject();

        public override string ToString()
        {
            string retVal = ProjectName;

            if (ProjectName == null)
                retVal = ProjectFile;

            return retVal;
        }

        public void TryInstallingGSD(string version)
        {
            //string siemensGsdPath =
            //    $"C:\\ProgramData\\Siemens\\Automation\\Portal V{version}\\data\\xdd\\gsd";

            //string[] gsdFiles = Directory.GetFiles(
            //    $"{ProjectFolder}AdditionalFiles\\GSD\\",
            //    "*.xml",
            //    SearchOption.AllDirectories
            //);
            //string[] bmpFiles = Directory.GetFiles(
            //    $"{ProjectFolder}AdditionalFiles\\GSD\\",
            //    "*.bmp",
            //    SearchOption.AllDirectories
            //);

            //foreach (string gsdFile in gsdFiles)
            //{
            //    string gsdName = gsdFile.Split('\\').Last().Replace(".xml", "");
            //    if (!Directory.Exists($"{siemensGsdPath}\\{gsdName}"))
            //    {
            //        Directory.CreateDirectory($"{siemensGsdPath}\\{gsdName}");
            //        File.Copy(gsdFile, $"{siemensGsdPath}\\{gsdName}\\{gsdName}.xml");

            //        string xml = File.ReadAllText(gsdFile);
            //        CopyBmpFile(xml, bmpFiles, $"{siemensGsdPath}\\{gsdName}\\");
            //    }
            //}
        }

        public void CopyBmpFile(string xmlFile, string[] bmpFiles, string path)
        {
            foreach (
                Match graphicsList in Regex.Matches(
                    xmlFile,
                    @"<GraphicsList>([\s\S]+?)</GraphicsList>"
                )
            )
            {
                foreach (
                    Match graphic in Regex.Matches(graphicsList.Value, @"GraphicFile=([\s\S]+?)/>")
                )
                {
                    string graphicName = graphic.Value.Split('"')[1];

                    foreach (string bmpFile in bmpFiles)
                        if (
                            bmpFile.Split('\\').Last().Replace(".bmp", "").ToUpper()
                            == graphicName.ToUpper()
                        )
                            File.Copy(bmpFile, $"{path}{bmpFile.Split('\\').Last()}");
                }
            }
        }
        public virtual void ExportTextlists(ProjectFolder folder, string exportPath)
        {
        }

    }
}
