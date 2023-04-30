using FastReport.Barcode;
using FastReport;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class FastReportBuilder
{
    private string ReportFilePath { get; set; } = default!;
    private List<ReportDataSet> ReportDataSets { get; set; } = new();

    private Dictionary<string, List<string>> EmptyDataBandsToHide = new Dictionary<string, List<string>> { };

    public FastReportBuilder() { }

    /// <summary>
    /// Hides the data band if it's Data List is empty.
    /// </summary>
    /// <param name="relatedBands">Name of other Bands that need to be hidden in case the Data Band is hidden.</param>
    /// <returns></returns>
    public FastReportBuilder HideDataBandIfEmpty(string dataBandName, params string[] relatedBands)
    {
        this.EmptyDataBandsToHide[dataBandName] = new List<string>(relatedBands);

        return this;
    }

    public FastReportBuilder AddFastReportFile(string reportFilePath)
    {
        this.ReportFilePath = reportFilePath;

        return this;
    }

    public FastReportBuilder AddDataList(string name, string dataBand, List<object> dataList, int maxNestingLevel = 3, string filterExpression = null)
    {
        this.ReportDataSets.Add(new ReportDataSet(
            name,
            dataBand,
            dataList,
            maxNestingLevel,
            filterExpression
        ));

        return this;
    }

    public FastReportBuilder AddDataObject(string name, object data)
    {
        this.ReportDataSets.Add(new ReportDataSet(
            name,
            null,
            new List<object> { data },
            3,
            null
        ));

        return this;
    }

    public async Task<FileStreamResult> GetPDFFile(Action<FastReport.Report> reportCustomizer = null)
    {
        return new FileStreamResult(await this.GetPDFStream(reportCustomizer), "application/pdf");
    }

    public async Task<Stream> GetPDFStream(Action<FastReport.Report> reportCustomizer = null)
    {
        if (ReportFilePath == null)
            throw new Exception($"ReportFilePath is not Added. Please add it using {nameof(AddFastReportFile)} method.");

        using (Report report = new Report())
        {
            FastReport.Utils.Config.WebMode = true;

            report.Load(this.ReportFilePath);

            foreach (var dataSet in this.ReportDataSets)
            {
                report.RegisterData(dataSet.Data, dataSet.Name, dataSet.MaxNestingLevel);

                var ds = report.GetDataSource(dataSet.Name);

                ds.Enabled = true;

                ds.Init();

                ds.EnsureInit();

                if (dataSet.DataBand == null)
                    continue;

                var band = report.FindObject(dataSet.DataBand) as FastReport.DataBand;

                if (band == null)
                    throw new Exception($"A band with The Name ({dataSet.DataBand}) does not exist on the report.");

                if (dataSet.Data.Count == 0)
                {
                    TraverseAllObjects(band.AllObjects, AllObjectTraverserAction.RemoveExpression);

                    if (this.EmptyDataBandsToHide.Keys.Contains(dataSet.DataBand))
                    {
                        band.Visible = false;

                        foreach (var relatedBand in this.EmptyDataBandsToHide[dataSet.DataBand])
                        {
                            var relatedBandObject = report.FindObject(relatedBand) as FastReport.BandBase;

                            if (relatedBandObject == null)
                                throw new Exception($"A band with The Name ({relatedBand}) does not exist on the report.");

                            relatedBandObject.Visible = false;
                        }
                    }

                    continue;
                }

                if (dataSet.FilterExpression != null)
                    band.Filter = dataSet.FilterExpression;

                band.DataSource = ds;
                band.InitDataSource();
            }

            TraverseAllObjects(report.AllObjects, AllObjectTraverserAction.AllowExpression);

            if (reportCustomizer != null)
                reportCustomizer(report);

            report.Prepare();

            MemoryStream stream = new MemoryStream();

            using (FastReport.Export.PdfSimple.PDFSimpleExport pdf = new FastReport.Export.PdfSimple.PDFSimpleExport())
            {
                report.Export(pdf, stream);
            }

            await stream.FlushAsync();

            stream.Seek(0, SeekOrigin.Begin);

            return stream;
        }
    }

    private void TraverseAllObjects(ObjectCollection objectCollection, AllObjectTraverserAction action)
    {
        foreach (var item in objectCollection)
        {
            var textObject = item as TextObject;

            if (textObject != null)
            {
                if (action == AllObjectTraverserAction.RemoveExpression && textObject.Text.StartsWith(textObject.Brackets.Substring(0, 1)))
                    textObject.Text = "";
                else if (action == AllObjectTraverserAction.AllowExpression)
                    textObject.AllowExpressions = true;
            }

            var barcodeObject = item as BarcodeObject;

            if (barcodeObject != null)
            {
                if (action == AllObjectTraverserAction.RemoveExpression && barcodeObject.Text.StartsWith(barcodeObject.Brackets.Substring(0, 1)))
                    barcodeObject.Text = "";
                else if (action == AllObjectTraverserAction.AllowExpression)
                    barcodeObject.AllowExpressions = true;
            }

            var pictureObject = item as PictureObject;

            if (pictureObject != null)
            {
                if (action == AllObjectTraverserAction.RemoveExpression)
                    pictureObject.ImageSourceExpression = "";
                else if (action == AllObjectTraverserAction.AllowExpression)
                    pictureObject.ImageSourceExpression = pictureObject.ImageLocation;
            }
        }
    }

    private enum AllObjectTraverserAction
    {
        RemoveExpression = 1,
        AllowExpression = 2
    }

    private class ReportDataSet
    {
        public string Name { get; set; }
        public string? DataBand { get; set; }
        public List<object> Data { get; set; }
        public int MaxNestingLevel { get; set; }
        public string FilterExpression { get; set; }

        public ReportDataSet(string name, string? dataBand, List<object> data, int maxNestingLevel, string? filterExpression)
        {
            this.Name = name;
            this.DataBand = dataBand;
            this.Data = data;
            this.MaxNestingLevel = maxNestingLevel;
            this.FilterExpression = filterExpression;
        }
    }
}
