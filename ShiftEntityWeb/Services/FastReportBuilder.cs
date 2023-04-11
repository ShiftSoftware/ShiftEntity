using FastReport.Barcode;
using FastReport;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class FastReportBuilder
{
    private string ReportFilePath { get; set; } = default!;
    private List<ReportDataSet> ReportDataSets { get; set; } = new();

    public FastReportBuilder() { }

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

    public async Task<FileStreamResult> GetPDFFile()
    {
        return new FileStreamResult(await this.GetPDFStream(), "application/pdf");
    }

    public async Task<Stream> GetPDFStream()
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

                if (dataSet.DataBand != null)
                {
                    var band = report.FindObject(dataSet.DataBand) as FastReport.DataBand;

                    if (band == null)
                        throw new Exception($"A band with The Name ({dataSet.DataBand}) does not exist on the report.");

                    if (dataSet.FilterExpression != null)
                        band.Filter = dataSet.FilterExpression;

                    band.DataSource = ds;
                    band.InitDataSource();
                }
            }

            foreach (var item in report.AllObjects)
            {
                var textObject = item as TextObject;

                if (textObject != null)
                    textObject.AllowExpressions = true;

                var barcodeObject = item as BarcodeObject;

                if (barcodeObject != null)
                    barcodeObject.AllowExpressions = true;
            }

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
