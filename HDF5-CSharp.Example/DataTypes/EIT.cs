﻿using HDF5CSharp.DataTypes;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Threading.Tasks;

namespace HDF5CSharp.Example.DataTypes
{
    [Hdf5GroupName("eit")]
    public class EIT : Hdf5BaseFile, IDisposable
    {
        [Hdf5EntryName("configuration")] public string Configuration { get; set; }
        [Hdf5EntryName("start_datetime")] public long? StartDateTime { get; set; }
        [Hdf5EntryName("end_datetime")] public long EndDateTime { get; set; }
        [Hdf5Save(Hdf5Save.DoNotSave)] private ChunkedDataset<float> VoltagesReal { get; set; }
        [Hdf5Save(Hdf5Save.DoNotSave)] private ChunkedDataset<float> VoltagesIm { get; set; }
        [Hdf5Save(Hdf5Save.DoNotSave)] private ChunkedDataset<float> CurrentsReal { get; set; }
        [Hdf5Save(Hdf5Save.DoNotSave)] private ChunkedDataset<float> CurrentsIm { get; set; }
        [Hdf5Save(Hdf5Save.DoNotSave)] private ChunkedDataset<ulong> Saturation { get; set; }
        [Hdf5Save(Hdf5Save.DoNotSave)] private ChunkedDataset<long> Timestamps { get; set; }
        [Hdf5Save(Hdf5Save.DoNotSave)] private BlockingCollectionQueue<ElectrodeFrame> ElectrodeSamplesData { get; set; }
        [Hdf5Save(Hdf5Save.DoNotSave)] private Task ElectrodeTaskWriter { get; set; }
        [Hdf5Save(Hdf5Save.DoNotSave)] private bool completed;
        [Hdf5Save(Hdf5Save.DoNotSave)] private int ChunkSize;

        public EIT(int recordNumber, int chunkSize, string acquisitionProtocol, long fileId, long groupRoot, ILogger logger) : base(fileId, groupRoot, "d" + recordNumber, logger)
        {
            ChunkSize = chunkSize;
            var pool = ArrayPool<ElectrodeFrame>.Shared;
            ElectrodeSamplesData = new BlockingCollectionQueue<ElectrodeFrame>();
            Configuration = acquisitionProtocol;
            VoltagesReal = new ChunkedDataset<float>("voltages.re", GroupId);
            VoltagesIm = new ChunkedDataset<float>("voltages.im", GroupId);
            CurrentsReal = new ChunkedDataset<float>("currents.re", GroupId);
            CurrentsIm = new ChunkedDataset<float>("currents.im", GroupId);
            Saturation = new ChunkedDataset<ulong>("saturations", GroupId);
            Timestamps = new ChunkedDataset<long>("timestamps", GroupId);
            ElectrodeTaskWriter = Task.Factory.StartNew(() =>
            {
                var buffer = pool.Rent(ChunkSize);
                completed = false;
                int count = 0;
                foreach (ElectrodeFrame data in ElectrodeSamplesData.GetConsumingEnumerable())
                {
                    buffer[count++] = data;
                    if (count == ChunkSize)
                    {
                        EndDateTime = data.timestamp;
                        AppendSample(buffer, chunkSize);
                        count = 0;
                    }
                }

                if (count != 0)
                {
                    EndDateTime = buffer[count - 1].timestamp;
                    AppendSample(buffer, count);

                }
                FlushData();//end of data samples. flush data
                pool.Return(buffer);
            });
        }

        private void AppendSample(ElectrodeFrame[] samples, int length)
        {
            float[,] vReData = new float[length, samples[0].ComplexVoltageMatrix.Length];
            float[,] vImData = new float[length, samples[0].ComplexVoltageMatrix.Length];
            float[,] cReData = new float[length, samples[0].ComplexCurrentMatrix.Length];
            float[,] cImData = new float[length, samples[0].ComplexCurrentMatrix.Length];
            ulong[,] saturationData = new ulong[length, 1];
            long[,] timestampData = new long[length, 1];
            for (var i = 0; i < length; i++)
            {
                ElectrodeFrame electrodeFrame = samples[i];
                for (int j = 0; j < electrodeFrame.ComplexVoltageMatrix.Length; j++)
                {
                    vReData[i, j] = electrodeFrame.ComplexVoltageMatrix[j].Re;
                    vImData[i, j] = electrodeFrame.ComplexVoltageMatrix[j].Im;
                }

                for (int j = 0; j < electrodeFrame.ComplexCurrentMatrix.Length; j++)
                {
                    cReData[i, j] = electrodeFrame.ComplexCurrentMatrix[j].Re;
                    cImData[i, j] = electrodeFrame.ComplexCurrentMatrix[j].Im;
                }

                saturationData[i, 0] = electrodeFrame.SaturationMask;
                timestampData[i, 0] = electrodeFrame.timestamp;
            }
            VoltagesReal.AppendOrCreateDataset(vReData);
            VoltagesIm.AppendOrCreateDataset(vImData);
            CurrentsReal.AppendOrCreateDataset(cReData);
            CurrentsIm.AppendOrCreateDataset(cImData);
            Saturation.AppendOrCreateDataset(saturationData);
            Timestamps.AppendOrCreateDataset(timestampData);
        }

        public void Dispose()
        {
            try
            {
                if (!Disposed)
                {
                    VoltagesReal.Dispose();
                    VoltagesIm.Dispose();
                    CurrentsReal?.Dispose();
                    CurrentsIm.Dispose();
                    Saturation.Dispose();
                    Timestamps.Dispose();
                    ElectrodeTaskWriter.Dispose();
                    Hdf5.CloseGroup(GroupId);
                    Disposed = true;
                }
            }
            catch (Exception e)
            {
                //do nothing
            }

        }

        public void Enqueue(ElectrodeFrame sample)
        {
            if (completed)
            {
                return;
            }

            if (!StartDateTime.HasValue)
            {
                StartDateTime = sample.timestamp;
            }

            ElectrodeSamplesData.Enqueue(sample);
        }

        public void CompleteAdding()
        {
            if (completed)
            {
                return;
            }

            completed = true;
            ElectrodeSamplesData.CompleteAdding();
        }

        public async Task WaitForDataWritten()
        {
            CompleteAdding();
            await ElectrodeTaskWriter;
        }
    }
}
