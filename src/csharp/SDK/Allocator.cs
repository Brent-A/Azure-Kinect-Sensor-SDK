﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Microsoft.Azure.Kinect.Sensor
{
    internal class Allocator
    {
        private readonly SortedDictionary<long, AllocationContext> allocations = new SortedDictionary<long, AllocationContext>();

        private NativeMethods.k4a_memory_allocate_cb_t allocateDelegate;
        private NativeMethods.k4a_memory_destroy_cb_t freeDelegate;
        private bool hooked;

        public static Allocator Singleton { get; } = new Allocator();

        private readonly HashSet<WeakReference<IDisposable>> disposables = new HashSet<WeakReference<IDisposable>>();

        public bool UseManagedAllocator { get; set; } = false;

        public bool CopyNativeBuffers { get; set; } = false;
        
        public void Hook(IDisposable disposable)
        {
            lock (this)
            {
                this.disposables.Add(new WeakReference<IDisposable>(disposable));

                if (this.hooked)
                {
                    return;
                }

                
                this.allocateDelegate = new NativeMethods.k4a_memory_allocate_cb_t(this.AllocateFunction);
                this.freeDelegate = new NativeMethods.k4a_memory_destroy_cb_t(this.FreeFunction);

                if (UseManagedAllocator)
                {
                    AzureKinectException.ThrowIfNotSuccess(NativeMethods.k4a_set_allocator(this.allocateDelegate, this.freeDelegate));
                }

                AppDomain.CurrentDomain.DomainUnload += this.CurrentDomain_DomainUnload;
                AppDomain.CurrentDomain.ProcessExit += this.CurrentDomain_DomainUnload;

                this.hooked = true;
            }
        }

        public void Unhook(IDisposable disposable)
        {
            lock (this)
            {
                disposables.RemoveWhere((r) => 
                    {
                        IDisposable target;
                        bool alive = r.TryGetTarget(out target);

                        return alive || target == disposable;
                });
            }
        }

        public Memory<byte> GetMemory(IntPtr address, long size)
        {
            lock (this)
            {
                AllocationContext allocation = this.FindNearestContext(address);
                if (allocation == null)
                {
                    return null;
                }

                long offset = (long)address - (long)allocation.BufferAddress;

                // Check that the beginning of the memory is in this allocation
                if (offset > allocation.Buffer.LongLength)
                {
                    return null;
                }

                // Check that the end of the memory is in this allocation
                if (checked(offset + size) > allocation.Buffer.LongLength)
                {
                    return null;
                }

                // Return a reference to this memory
                return new Memory<byte>(allocation.Buffer, checked((int)offset), checked((int)size));
            }
        }

        private AllocationContext FindNearestContext(IntPtr address)
        {
            lock (this)
            {
                long[] keys = new long[this.allocations.Count];
                this.allocations.Keys.CopyTo(keys, 0);

                int searchIndex = Array.BinarySearch(keys, (long)address);
                if (searchIndex >= 0)
                {
                    return this.allocations[keys[searchIndex]];
                }
                else
                {
                    int nextLowestIndex = ~searchIndex - 1;
                    if (nextLowestIndex < 0)
                    {
                        return null;
                    }

                    AllocationContext allocation = this.allocations[keys[nextLowestIndex]];

                    return allocation;
                }
            }
        }

        private IntPtr AllocateFunction(int size, out IntPtr context)
        {
            byte[] buffer = new byte[size];
            AllocationContext allocationContext = new AllocationContext()
            {
                Buffer = buffer,
                BufferPin = GCHandle.Alloc(buffer, GCHandleType.Pinned),
                CallbackDelegate = this.freeDelegate,
            };

            allocationContext.BufferAddress = allocationContext.BufferPin.AddrOfPinnedObject();

            context = (IntPtr)GCHandle.Alloc(allocationContext);

            lock (this)
            {
                this.allocations.Add((long)allocationContext.BufferAddress, allocationContext);
            }

            return allocationContext.BufferAddress;
        }

        private void FreeFunction(IntPtr buffer, IntPtr context)
        {
            GCHandle contextPin = (GCHandle)context;
            AllocationContext allocationContext = (AllocationContext)contextPin.Target;

            lock (this)
            {
                System.Diagnostics.Debug.Assert(object.ReferenceEquals(this.allocations[(long)buffer], allocationContext), "Allocation context does not match expected value");
                this.allocations.Remove((long)buffer);
            }

            allocationContext.BufferPin.Free();
            contextPin.Free();
        }

        private void CurrentDomain_DomainUnload(object sender, EventArgs e)
        {
            lock (this)
            {
                
                foreach (var r in disposables)
                {
                    IDisposable disposable;
                    if (r.TryGetTarget(out disposable))
                    {
                        disposable.Dispose();
                    }
                }
                disposables.Clear();

                if (this.allocations.Count > 0)
                {
                    throw new Exception("Not all native allocations have been freed before managed shutdown");
                }
            }
        }

        private class AllocationContext
        {
            public byte[] Buffer { get; set; }

            public IntPtr BufferAddress { get; set; }

            public GCHandle BufferPin { get; set; }

            public NativeMethods.k4a_memory_destroy_cb_t CallbackDelegate { get; set; }
        }
    }


}
