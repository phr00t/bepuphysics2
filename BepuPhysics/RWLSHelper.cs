using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace BepuPhysics.Threading {
    public static class RWLSHelper {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ReadLockHelper ReadLock(this ReaderWriterLockSlim readerWriterLock) {
            return new ReadLockHelper(readerWriterLock);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static UpgradeableReadLockHelper UpgradableReadLock(this ReaderWriterLockSlim readerWriterLock) {
            return new UpgradeableReadLockHelper(readerWriterLock);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WriteLockHelper WriteLock(this ReaderWriterLockSlim readerWriterLock) {
            return new WriteLockHelper(readerWriterLock);
        }

        public struct ReadLockHelper : IDisposable {
            private readonly ReaderWriterLockSlim readerWriterLock;

            public ReadLockHelper(ReaderWriterLockSlim readerWriterLock) {
                readerWriterLock.EnterReadLock();
                this.readerWriterLock = readerWriterLock;
            }

            public void Dispose() {
                this.readerWriterLock.ExitReadLock();
            }
        }

        public struct UpgradeableReadLockHelper : IDisposable {
            private readonly ReaderWriterLockSlim readerWriterLock;

            public UpgradeableReadLockHelper(ReaderWriterLockSlim readerWriterLock) {
                readerWriterLock.EnterUpgradeableReadLock();
                this.readerWriterLock = readerWriterLock;
            }

            public void Dispose() {
                this.readerWriterLock.ExitUpgradeableReadLock();
            }
        }

        public struct WriteLockHelper : IDisposable {
            private readonly ReaderWriterLockSlim readerWriterLock;

            public WriteLockHelper(ReaderWriterLockSlim readerWriterLock) {
                readerWriterLock.EnterWriteLock();
                this.readerWriterLock = readerWriterLock;
            }

            public void Dispose() {
                this.readerWriterLock.ExitWriteLock();
            }
        }
    }
}
